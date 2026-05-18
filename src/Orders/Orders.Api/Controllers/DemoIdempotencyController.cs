using Haworks.Orders.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Haworks.Orders.Api.Controllers;

/// <summary>
/// Demo-only idempotency endpoint for the portfolio site.
///
/// This is the same idempotency pattern the real Orders aggregate uses:
/// a Postgres UNIQUE-constrained column on a key, with INSERT...ON CONFLICT
/// resolving concurrent writers. The Orders.Orders table has a UNIQUE
/// index on IdempotencyKey already; this controller exposes a minimal
/// demo-friendly variant of the same mechanism (a single small table)
/// so the portfolio's IdempotencyDemo proves real PG-level concurrency
/// rather than an in-process ConcurrentDictionary.
///
/// Schema is created lazily on first request (CREATE TABLE IF NOT EXISTS)
/// to avoid coupling the demo to a real EF migration. Pruning expired
/// rows is also opportunistic; the demo doesn't accumulate enough data
/// for that to matter.
/// </summary>
[ApiController]
[Route("demo/idempotency")]
[AllowAnonymous]
public sealed class DemoIdempotencyController(
    OrderDbContext db,
    ILogger<DemoIdempotencyController> logger) : ControllerBase
{
    private static int s_tableInitialized;

    /// <summary>
    /// Atomically claim an idempotency key. Returns the same claim id
    /// for every caller of the same key within the TTL window —
    /// the real-system mechanism (Postgres UNIQUE + ON CONFLICT) doing
    /// the dedup, not in-process state.
    /// </summary>
    [HttpPost("claim")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Claim(
        [FromHeader(Name = "X-Idempotency-Key")] string? key,
        [FromQuery] int ttlSeconds = 30,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(new { error = "X-Idempotency-Key header is required" });
        }
        if (ttlSeconds < 5) ttlSeconds = 5;
        if (ttlSeconds > 600) ttlSeconds = 600;

        try
        {
            await EnsureTableAsync(ct);

            // Best-effort prune of expired rows. Cheap; the demo table is tiny.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM demo_idempotency_keys WHERE expires_at < NOW()",
                ct);

            var newClaimId = Guid.NewGuid();
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);

            // The dedup mechanism is the UNIQUE constraint on `key`.
            // ON CONFLICT (key) DO UPDATE SET key = key (no-op) lets us
            // RETURNING the existing row even on conflict; xmax = 0
            // distinguishes the new row from the existing one.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO demo_idempotency_keys (key, claim_id, created_at, expires_at)
                VALUES (@key, @claim_id, NOW(), NOW() + (@ttl || ' seconds')::interval)
                ON CONFLICT (key) DO UPDATE SET key = EXCLUDED.key
                RETURNING claim_id, (xmax = 0) AS is_winner, created_at, expires_at
                """;
            AddParam(cmd, "key", key);
            AddParam(cmd, "claim_id", newClaimId);
            AddParam(cmd, "ttl", ttlSeconds.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return StatusCode(500, new { error = "Idempotency claim returned no row" });
            }

            var claimId = reader.GetGuid(0);
            var isWinner = reader.GetBoolean(1);
            var createdAt = reader.GetDateTime(2);
            var expiresAt = reader.GetDateTime(3);

            return Ok(new
            {
                idempotencyKey = key,
                claimId,
                isDuplicate = !isWinner,
                isWinner,
                keyInfo = new
                {
                    createdAt,
                    expiresAt,
                    ttlSeconds = (int)(expiresAt - createdAt).TotalSeconds,
                },
                cacheAgeSeconds = (int)Math.Max(0, (DateTime.UtcNow - createdAt).TotalSeconds),
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Idempotency claim failed for key={Key}", key);
            // Honest failure: surfaces as a real 503 to the BFF/topology
            // when postgres is paused via the chaos panel.
            return StatusCode(503, new
            {
                error = "Idempotency store unreachable",
                message = ex.Message,
            });
        }
    }

    /// <summary>
    /// Concurrent-race demo: fires N parallel claims of the same key
    /// against the real DB and returns who won. The single winner ==
    /// real Postgres UNIQUE constraint behaviour, not a simulation.
    /// </summary>
    [HttpPost("race")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Race(
        [FromBody] RaceRequest body,
        CancellationToken ct)
    {
        var count = Math.Clamp(body.Count, 2, 10);
        var ttlSeconds = Math.Clamp(body.TtlSeconds ?? 30, 5, 600);
        if (string.IsNullOrWhiteSpace(body.Key))
        {
            return BadRequest(new { error = "key is required" });
        }

        await EnsureTableAsync(ct);

        // Clear any prior claim of this exact key so the race always has
        // a fresh row to fight over.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM demo_idempotency_keys WHERE key = {0}", new object[] { body.Key! }, ct);

        var connStr = db.Database.GetConnectionString();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var conn = new Npgsql.NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO demo_idempotency_keys (key, claim_id, created_at, expires_at)
                    VALUES (@key, @claim_id, NOW(), NOW() + (@ttl || ' seconds')::interval)
                    ON CONFLICT (key) DO UPDATE SET key = EXCLUDED.key
                    RETURNING claim_id, (xmax = 0) AS is_winner
                    """;
                AddParam(cmd, "key", body.Key);
                AddParam(cmd, "claim_id", Guid.NewGuid());
                AddParam(cmd, "ttl", ttlSeconds.ToString());

                using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                var claimId = reader.GetGuid(0);
                var isWinner = reader.GetBoolean(1);
                sw.Stop();
                return new
                {
                    requestIndex = i,
                    isWinner,
                    orderId = claimId,
                    durationMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogWarning(ex, "Race attempt {Index} failed", i);
                return new
                {
                    requestIndex = i,
                    isWinner = false,
                    orderId = Guid.Empty,
                    durationMs = sw.ElapsedMilliseconds,
                };
            }
        });

        var outcomes = await Task.WhenAll(tasks);
        return Ok(new
        {
            key = body.Key,
            count,
            ttlSeconds,
            outcomes = outcomes.OrderBy(o => o.requestIndex).ToArray(),
        });
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref s_tableInitialized) == 1) return;
        // Idempotent — CREATE TABLE IF NOT EXISTS is safe under concurrent
        // requests; the Volatile flag just avoids the extra DDL round-trip
        // after the first call has succeeded.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS demo_idempotency_keys (
                key VARCHAR(200) PRIMARY KEY,
                claim_id UUID NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                expires_at TIMESTAMPTZ NOT NULL
            )
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

    public sealed record RaceRequest(string Key, [property: System.Text.Json.Serialization.JsonRequired] int Count, int? TtlSeconds);
}
