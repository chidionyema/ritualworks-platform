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
    /// connection string. Caller scopes the database to its fixture so
    /// tests in different assemblies cannot collide.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(string serviceName)
    {
        var container = await GetAsync();
        var dbName = $"{serviceName}_{Guid.NewGuid():N}".ToLowerInvariant();
        var adminConn = container.GetConnectionString();
        await using (var conn = new NpgsqlConnection(adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var b = new NpgsqlConnectionStringBuilder(adminConn) { Database = dbName };
        return b.ConnectionString;
    }
}
