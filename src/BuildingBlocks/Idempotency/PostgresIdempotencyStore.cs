using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// EF-Core-on-Postgres implementation of <see cref="IIdempotencyStore"/>.
/// Generic over the bounded-context DbContext so each service stores its
/// claims in its own database, preserving the bounded-context isolation
/// rule (no cross-context table sharing).
///
/// The actual dedup is the UNIQUE constraint on <c>idempotency_claims.key</c>,
/// resolved by INSERT...ON CONFLICT. xmax = 0 distinguishes the inserter
/// from a caller that just saw the existing row.
///
/// Schema is created lazily on first call (CREATE TABLE IF NOT EXISTS) so
/// the middleware doesn't require a service-specific migration to be wired
/// in. Same pattern as Orders' DemoIdempotencyController.
/// </summary>
public sealed class PostgresIdempotencyStore<TDbContext> : IIdempotencyStore
    where TDbContext : DbContext
{
    private static int s_tableInitialized;

    private readonly TDbContext _db;

    public PostgresIdempotencyStore(TDbContext db) => _db = db;

    public async Task<IdempotencyClaim> TryClaimAsync(
        string key,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await EnsureTableAsync(ct);

        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        // Best-effort prune (cheap; the table is small in steady state).
        await using (var prune = conn.CreateCommand())
        {
            prune.CommandText = "DELETE FROM idempotency_claims WHERE expires_at < NOW()";
            await prune.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO idempotency_claims (key, claim_id, created_at, expires_at)
            VALUES (@key, @claim_id, NOW(), NOW() + (@ttl_seconds || ' seconds')::interval)
            ON CONFLICT (key) DO UPDATE SET key = EXCLUDED.key
            RETURNING (xmax = 0) AS is_winner
            """;
        AddParam(cmd, "key", key);
        AddParam(cmd, "claim_id", Guid.NewGuid());
        AddParam(cmd, "ttl_seconds", ((int)ttl.TotalSeconds).ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            // Should never happen for INSERT...RETURNING; treat as winner so
            // we don't accidentally block legitimate first-time requests.
            return IdempotencyClaim.Winner;
        }

        var isWinner = reader.GetBoolean(0);
        return isWinner ? IdempotencyClaim.Winner : IdempotencyClaim.Duplicate;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref s_tableInitialized) == 1) return;
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS idempotency_claims (
                key VARCHAR(200) PRIMARY KEY,
                claim_id UUID NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_idempotency_claims_expires_at
                ON idempotency_claims (expires_at);
            """,
            ct);
        Volatile.Write(ref s_tableInitialized, 1);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
