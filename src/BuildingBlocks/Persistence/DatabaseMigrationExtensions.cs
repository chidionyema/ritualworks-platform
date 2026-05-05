using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Haworks.BuildingBlocks.Persistence;

/// <summary>
/// Apply pending EF migrations with bounded retry for the Postgres
/// startup window. Postgres returns SQLSTATE <c>57P03</c>
/// ("the database system is starting up") for several seconds after a
/// container restart while it recovers state from its data volume.
/// Services that hit migrate during that window crash with exit 134;
/// without retry the service stays dead until manual intervention.
///
/// Pattern is identical across all services owning a DbContext —
/// extracting it to BuildingBlocks keeps the per-service Program.cs
/// down to one line and ensures we use the same retry policy everywhere.
/// </summary>
public static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Calls <c>DatabaseFacade.MigrateAsync</c> inside a Polly retry that
    /// handles transient Postgres startup errors.
    /// Logs each retry attempt at Warning so dev sees the recovery
    /// without it being silent. Total budget: 8 attempts, ~30s wall-clock
    /// (2^attempt seconds, capped at 5s, with jitter).
    /// </summary>
    public static async Task MigrateWithRetryAsync(
        this DatabaseFacade database,
        ILogger logger,
        CancellationToken ct = default)
    {
        var policy = BuildPolicy(logger);
        await policy.ExecuteAsync(async (innerCt) =>
        {
            await database.MigrateAsync(innerCt);
        }, ct);
    }

    private static AsyncRetryPolicy BuildPolicy(ILogger logger) =>
        Policy
            .Handle<Exception>(IsTransientPostgresStartup)
            .WaitAndRetryAsync(
                retryCount: 8,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 5))
                    + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)),
                onRetry: (ex, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        ex,
                        "EF migration retry {Attempt}/8 after {Delay}ms — {ExceptionType}: {Message}",
                        attempt, (int)delay.TotalMilliseconds,
                        ex.GetType().Name, ex.Message);
                });

    /// <summary>
    /// Recognises the conditions a fresh Postgres container exhibits during
    /// its warm-up window. Match by SQLSTATE without taking a hard reference
    /// to Npgsql (BuildingBlocks shouldn't pin a specific driver). The
    /// Postgres protocol exposes SqlState as a property; we duck-type it.
    ///
    /// SQLSTATE codes we treat as retryable:
    ///   57P03 the_database_system_is_starting_up
    ///   57P02 crash_shutdown
    ///   08006 connection_failure
    ///   08001 sqlclient_unable_to_establish_sqlconnection
    /// Plus any unwrapped socket / connection-refused exceptions Npgsql
    /// throws before the protocol layer is reached.
    /// </summary>
    private static bool IsTransientPostgresStartup(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "PostgresException")
            {
                var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
                if (sqlState is "57P03" or "57P02" or "08006" or "08001")
                {
                    return true;
                }
            }

            // Pre-protocol failures (TCP connect refused, DNS, etc.) surface
            // as SocketException or NpgsqlException without a SqlState. The
            // database container's port is bound by Aspire's DCP proxy from
            // the moment the AppHost starts, so connect-refused here means
            // Postgres itself isn't yet listening — same retry semantics as 57P03.
            var typeName = current.GetType().Name;
            if (typeName is "SocketException" or "NpgsqlException")
            {
                if (current.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                    current.Message.Contains("server closed the connection", StringComparison.OrdinalIgnoreCase) ||
                    current.Message.Contains("starting up", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
