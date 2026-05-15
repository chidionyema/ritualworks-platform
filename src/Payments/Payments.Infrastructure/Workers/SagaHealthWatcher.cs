using Haworks.Payments.Application.Telemetry;
using Haworks.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Infrastructure.Workers;

/// <summary>
/// Polls for stuck payment sagas (refund RequiresReview, dunning-exhausted
/// subscriptions) and increments OTel counters + logs critical alerts so
/// Grafana can fire notifications.
/// </summary>
public sealed class SagaHealthWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefundStuckThreshold = TimeSpan.FromHours(1);
    private const int DunningRetryThreshold = 3;

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
                _logger.LogError(ex, "SagaHealthWatcher tick failed; will retry next interval");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        await CheckRefundStuck(db, ct);
        await CheckDunningExhausted(db, ct);
    }

    private async Task CheckRefundStuck(PaymentDbContext db, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow - RefundStuckThreshold;

        var stuck = await db.RefundSagas
            .Where(s => s.CurrentState == "RequiresReview" && s.CreatedAt < deadline)
            .Select(s => new { s.CorrelationId, s.OrderId, s.Amount, s.CreatedAt })
            .ToListAsync(ct);

        foreach (var saga in stuck)
        {
            PaymentsActivities.RefundStuckInReview.Add(1);
            _logger.LogCritical(
                "Refund saga {SagaId} stuck in RequiresReview for order {OrderId}, amount {Amount}, since {CreatedAt}",
                saga.CorrelationId, saga.OrderId, saga.Amount, saga.CreatedAt);
        }
    }

    private async Task CheckDunningExhausted(PaymentDbContext db, CancellationToken ct)
    {
        var exhausted = await db.SubscriptionSagas
            .Where(s => s.CurrentState == "Canceled" && s.RetryCount > DunningRetryThreshold)
            .Select(s => new { s.CorrelationId, s.UserId, s.PlanId, s.RetryCount, s.LastModifiedAt })
            .ToListAsync(ct);

        foreach (var saga in exhausted)
        {
            PaymentsActivities.SubscriptionDunningExhausted.Add(1);
            _logger.LogCritical(
                "Subscription {SagaId} canceled by dunning exhaustion for user {UserId}, plan {PlanId}, retries {RetryCount}, last modified {LastModifiedAt}",
                saga.CorrelationId, saga.UserId, saga.PlanId, saga.RetryCount, saga.LastModifiedAt);
        }
    }
}
