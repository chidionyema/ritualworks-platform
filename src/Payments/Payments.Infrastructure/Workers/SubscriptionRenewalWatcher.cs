using Haworks.Contracts.Payments;
using Haworks.Payments.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Infrastructure.Workers;

/// <summary>
/// Belt-and-braces fallback for the saga's renewal timeout schedule.
///
/// The primary timeout is wired via MassTransit's
/// <c>Schedule&lt;SubscriptionRenewalScheduled&gt;</c> on the saga, which
/// delegates to the broker's delayed-message mechanism. If the plugin is
/// missing or the broker silently drops the scheduled message, sagas in
/// Active state past their PeriodEnd will sit indefinitely without a
/// renewal attempt.
///
/// This watcher polls every 5 minutes, finds any Active saga whose
/// PeriodEnd has passed, and publishes a
/// <see cref="SubscriptionRenewalRequestedEvent"/> directly. The saga's
/// existing handler transitions to Renewing and kicks off the renewal
/// flow — so this is purely additive. If the scheduler already fired,
/// the saga is in Renewing/GracePeriod/Canceled and doesn't match the
/// query.
///
/// Late duplicates (scheduler + watcher both fire) hit a saga already
/// transitioned out of Active, so the <c>When()</c> guard doesn't match
/// and MT logs a warning rather than double-renewing. Idempotent by
/// construction.
/// </summary>
public sealed class SubscriptionRenewalWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private const int MaxPublishesPerTick = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionRenewalWatcher> _logger;

    public SubscriptionRenewalWatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<SubscriptionRenewalWatcher> logger)
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "{Watcher} crashed — background processing stopped", nameof(SubscriptionRenewalWatcher));
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
            _logger.LogError(ex, "SubscriptionRenewalWatcher tick failed; will retry next interval");
        }

        try { await Task.Delay(PollInterval, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var now = DateTime.UtcNow;

        var overdue = await db.Set<SubscriptionSagaState>()
            .Where(s =>
                s.CurrentState == "Active" &&
                s.PeriodEnd < now)
            .OrderBy(s => s.PeriodEnd)
            .Take(MaxPublishesPerTick)
            .Select(s => new { s.CorrelationId, s.ProviderSubscriptionId })
            .ToListAsync(ct);

        if (overdue.Count == 0) return;

        _logger.LogInformation(
            "SubscriptionRenewalWatcher: publishing RenewalRequested for {Count} overdue saga(s)",
            overdue.Count);

        foreach (var saga in overdue)
        {
            try
            {
                await publishEndpoint.Publish(
                    new SubscriptionRenewalRequestedEvent
                    {
                        SubscriptionId = saga.CorrelationId,
                        ProviderSubscriptionId = saga.ProviderSubscriptionId,
                    },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SubscriptionRenewalWatcher: failed to publish for saga {SagaId}; will retry next tick",
                    saga.CorrelationId);
            }
        }
    }
}
