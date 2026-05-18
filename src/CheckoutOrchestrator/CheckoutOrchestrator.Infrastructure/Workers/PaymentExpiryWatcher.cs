using Haworks.CheckoutOrchestrator.Domain;
using Haworks.Contracts.Checkout;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.CheckoutOrchestrator.Infrastructure.Workers;

/// <summary>
/// Belt-and-braces fallback for the saga's payment-expiry timeout.
///
/// The primary timeout is wired via MassTransit's
/// <c>Schedule&lt;PaymentExpiredEvent&gt;</c> on the saga, which delegates
/// to the broker's delayed-message mechanism (RabbitMQ
/// rabbitmq_delayed_message_exchange plugin). If that plugin is missing
/// or the broker silently drops the scheduled message, sagas in
/// StockReserved/ReadyForPayment past their 15-min deadline will sit
/// indefinitely with stock reserved.
///
/// This watcher polls every 60s, finds any stuck saga past its deadline,
/// and publishes a <see cref="PaymentExpiredEvent"/> directly. The saga's
/// existing handler runs the same compensation as the scheduled path
/// (publish StockReleaseRequested + transition Abandoned), so this is
/// purely additive: if the scheduler works the watcher is a no-op
/// because the state is already Abandoned by the time it polls.
///
/// Late duplicates (scheduler + watcher both fire) hit a saga already
/// transitioned out of StockReserved/ReadyForPayment, so the
/// <c>When()</c> guard doesn't match and MT logs a warning rather than
/// double-compensating. Idempotent by construction.
/// </summary>
public sealed class PaymentExpiryWatcher : BackgroundService
{
    private static readonly TimeSpan ExpiryDeadline = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    // Hard cap on how many sagas we publish per tick. Protects against a
    // pathological backlog flooding the bus on first run after an outage.
    private const int MaxPublishesPerTick = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentExpiryWatcher> _logger;

    public PaymentExpiryWatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentExpiryWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Stagger first run so the watcher doesn't fire during cold-start
            // app initialization (services still registering, MT bus warming up).
            if (!await DelaySafeAsync(TimeSpan.FromSeconds(15), stoppingToken)) return;

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
            _logger.LogCritical(ex, "Unhandled exception in background service {ServiceName}", nameof(PaymentExpiryWatcher));
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
            _logger.LogError(ex, "PaymentExpiryWatcher tick failed; will retry next interval");
        }

        try { await Task.Delay(PollInterval, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var deadline = DateTime.UtcNow - ExpiryDeadline;

        // States named to match the saga's state names. CurrentState is the
        // string form MT writes — see CheckoutSaga.StockReservedState /
        // ReadyForPayment property names.
        var stuck = await db.Set<CheckoutSagaState>()
            .Where(s =>
                (s.CurrentState == "StockReservedState" || s.CurrentState == "ReadyForPayment") &&
                s.CreatedAt < deadline)
            .OrderBy(s => s.CreatedAt)
            .Take(MaxPublishesPerTick)
            .Select(s => new { s.CorrelationId, s.OrderId })
            .ToListAsync(ct);

        if (stuck.Count == 0) return;

        _logger.LogInformation(
            "PaymentExpiryWatcher: publishing PaymentExpired for {Count} stuck saga(s)",
            stuck.Count);

        foreach (var saga in stuck)
        {
            try
            {
                await publishEndpoint.Publish(
                    new PaymentExpiredEvent
                    {
                        SagaId = saga.CorrelationId,
                        OrderId = saga.OrderId,
                    },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PaymentExpiryWatcher: failed to publish for saga {SagaId}; will retry next tick",
                    saga.CorrelationId);
            }
        }
    }
}
