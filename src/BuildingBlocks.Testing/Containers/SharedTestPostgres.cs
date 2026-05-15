using Npgsql;
using Testcontainers.PostgreSql;

namespace Haworks.BuildingBlocks.Testing.Containers;

/// <summary>
/// Lazy-singleton Postgres container shared across every backend
/// integration assembly. <c>WithReuse(true)</c> means Testcontainers picks
/// the same container across <c>dotnet test</c> invocations as long as the
/// builder config hash is unchanged — keep this builder static and
/// identical, do not parameterize.
/// </summary>
public static class SharedTestPostgres
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static PostgreSqlContainer? _container;

    private static async Task<PostgreSqlContainer> GetAsync()
    {
        if (_container is { State: DotNet.Testcontainers.Containers.TestcontainersStates.Running })
            return _container;
        await _gate.WaitAsync();
        try
        {
            if (_container is null)
            {
                _container = new PostgreSqlBuilder()
                    .WithImage("postgres:16-alpine")
                    .WithDatabase("template")
                    .WithUsername("postgres")
                    .WithPassword("postgres")
                    .WithReuse(true)
                    .Build();
            }
            if (_container.State != DotNet.Testcontainers.Containers.TestcontainersStates.Running)
                await _container.StartAsync();
            return _container;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Creates a fresh database on the shared container and returns its
    /// connection string. Also cleans up orphaned databases from previous
    /// runs to prevent unbounded disk growth on the reused container.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(string serviceName)
    {
        var container = await GetAsync();
        var adminConn = container.GetConnectionString();

        // Clean up orphaned databases from previous test runs (keeps last 3 per service).
        // Runs BEFORE creating the new DB to avoid killing active connections.
        try { await CleanupOrphanedDatabasesAsync(adminConn, serviceName); } catch { /* best effort */ }

        var dbName = $"{serviceName}_{Guid.NewGuid():N}".ToLowerInvariant();
        await using (var conn = new NpgsqlConnection(adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var b = new NpgsqlConnectionStringBuilder(adminConn) { Database = dbName };
        return b.ConnectionString;
    }

    /// <summary>
    /// Drops a database created by <see cref="CreateDatabaseAsync"/>.
    /// Call from test factory's DisposeAsync for deterministic cleanup.
    /// </summary>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var dbName = builder.Database;
        if (string.IsNullOrEmpty(dbName) || dbName == "template") return;

        var container = await GetAsync();
        var adminConn = container.GetConnectionString();
        await using var conn = new NpgsqlConnection(adminConn);
        await conn.OpenAsync();
        // Terminate active connections before dropping
        await using var term = new NpgsqlCommand(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{dbName}' AND pid <> pg_backend_pid()", conn);
        await term.ExecuteNonQueryAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", conn);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task CleanupOrphanedDatabasesAsync(string adminConn, string serviceName)
    {
        try
        {
            await using var conn = new NpgsqlConnection(adminConn);
            await conn.OpenAsync();
            // Find all databases for this service, ordered oldest first
            await using var list = new NpgsqlCommand(
                $"SELECT datname FROM pg_database WHERE datname LIKE '{serviceName}_%' ORDER BY oid ASC", conn);
            var databases = new List<string>();
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync()) databases.Add(reader.GetString(0));
            await reader.CloseAsync();

            // Keep last 3, drop the rest
            var toDrop = databases.SkipLast(3).ToList();
            foreach (var db in toDrop)
            {
                try
                {
                    await using var term = new NpgsqlCommand(
                        $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()", conn);
                    await term.ExecuteNonQueryAsync();
                    await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{db}\"", conn);
                    await drop.ExecuteNonQueryAsync();
                }
                catch { /* best effort — don't fail tests for cleanup issues */ }
            }
        }
        catch { /* best effort */ }
    }
}
