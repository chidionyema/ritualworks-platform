using Npgsql;
using Testcontainers.PostgreSql;

namespace Haworks.BuildingBlocks.Testing.Containers;

/// <summary>
/// Lazy-singleton PostGIS container for services that need geospatial
/// extensions (e.g. Location). Same reuse pattern as <see cref="SharedTestPostgres"/>.
/// </summary>
public static class SharedTestPostGIS
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
                    .WithImage("postgis/postgis:16-3.4-alpine")
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
            await using var ext = new NpgsqlCommand($"CREATE EXTENSION IF NOT EXISTS postgis", conn);
            await ext.ExecuteNonQueryAsync();
        }
        var b = new NpgsqlConnectionStringBuilder(adminConn) { Database = dbName };
        return b.ConnectionString;
    }
}
