using System.Data.Common;
using Npgsql;
using Respawn;

namespace Haworks.BuildingBlocks.Testing;

public sealed class DatabaseResetter
{
    private Respawner? _respawner;
    private readonly string _connectionString;
    private readonly string[] _schemasToInclude;

    public DatabaseResetter(string connectionString, params string[] schemasToInclude)
    {
        _connectionString = connectionString;
        _schemasToInclude = schemasToInclude.Length > 0 ? schemasToInclude : ["public"];
    }

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        if (_respawner == null)
        {
            _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = _schemasToInclude,
                TablesToIgnore = ["__EFMigrationsHistory"]
            });
        }

        await _respawner.ResetAsync(connection);
    }
}
