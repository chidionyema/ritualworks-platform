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
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ErasureHealthWatcher tick failed; will retry next interval");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
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
