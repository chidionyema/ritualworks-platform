using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Application.Telemetry;
using Haworks.Privacy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Privacy.Infrastructure.Workers;

/// <summary>
/// Polls for privacy erasure requests stuck in Processing or Stalled state
/// past 24 hours and increments OTel counters + logs critical GDPR compliance
/// alerts for Grafana alerting.
/// </summary>
public sealed class ErasureHealthWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StalledThreshold = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ErasureHealthWatcher> _logger;

    public ErasureHealthWatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<ErasureHealthWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await DelaySafeAsync(TimeSpan.FromSeconds(30), stoppingToken)) return;

            while (!stoppingToken.IsCancellationRequested)
            {
                await TickSafeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled exception in background service {ServiceName}", nameof(ErasureHealthWatcher));
            throw;
        }
    }

    private static async Task<bool> DelaySafeAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private async Task TickSafeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ErasureHealthWatcher tick failed; will retry next interval");
        }

        try { await Task.Delay(PollInterval, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrivacyDbContext>();

        var deadline = DateTime.UtcNow - StalledThreshold;

        var stuck = await db.Set<PrivacyRequestState>()
            .Where(s =>
                (s.CurrentState == "Processing" || s.CurrentState == "Stalled") &&
                s.CreatedAt < deadline)
            .Select(s => new { s.CorrelationId, s.UserId, s.CurrentState, s.CreatedAt })
            .OrderBy(s => s.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        foreach (var saga in stuck)
        {
            PrivacyActivities.ErasureStalled.Add(1);
            _logger.LogCritical(
                "GDPR erasure request {RequestId} for user {UserId} stuck in {State} since {CreatedAt} — 30-day compliance deadline at risk",
                saga.CorrelationId, saga.UserId, saga.CurrentState, saga.CreatedAt);
        }
    }
}
