using Haworks.CheckoutOrchestrator.Application.Telemetry;
using Haworks.CheckoutOrchestrator.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.CheckoutOrchestrator.Infrastructure.Workers;

/// <summary>
/// Polls for checkout sagas stuck in RequiresReview and increments OTel
/// counters + logs critical alerts for Grafana alerting.
/// </summary>
public sealed class SagaHealthWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SagaHealthWatcher> _logger;

    public SagaHealthWatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<SagaHealthWatcher> logger)
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
            _logger.LogCritical(ex, "Unhandled exception in background service {ServiceName}", nameof(SagaHealthWatcher));
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
            _logger.LogError(ex, "SagaHealthWatcher tick failed; will retry next interval");
        }

        try { await Task.Delay(PollInterval, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();

        var deadline = DateTime.UtcNow - StuckThreshold;

        var stuck = await db.CheckoutSagas
            .Where(s => s.CurrentState == "RequiresReview" && s.CreatedAt < deadline)
            .Select(s => new { s.CorrelationId, s.OrderId, s.CreatedAt })
            .ToListAsync(ct);

        foreach (var saga in stuck)
        {
            CheckoutActivities.CheckoutStuckInReview.Add(1);
            _logger.LogCritical(
                "Checkout saga {SagaId} stuck in RequiresReview for order {OrderId} since {CreatedAt}",
                saga.CorrelationId, saga.OrderId, saga.CreatedAt);
        }
    }
}
