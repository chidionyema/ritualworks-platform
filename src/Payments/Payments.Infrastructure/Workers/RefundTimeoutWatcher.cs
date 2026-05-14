using Haworks.Payments.Domain;
using Haworks.Contracts.Payments;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Infrastructure.Workers;

public sealed class RefundTimeoutWatcher : BackgroundService
{
    private static readonly TimeSpan ExpiryDeadline = TimeSpan.FromHours(24);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);
    private const int MaxPublishesPerTick = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RefundTimeoutWatcher> _logger;

    public RefundTimeoutWatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<RefundTimeoutWatcher> logger)
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
                _logger.LogError(ex, "RefundTimeoutWatcher tick failed; will retry next interval");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var deadline = DateTime.UtcNow - ExpiryDeadline;

        var stuck = await db.RefundSagas
            .Where(s =>
                s.CurrentState == "AwaitingProviderConfirmation" &&
                s.CreatedAt < deadline)
            .OrderBy(s => s.CreatedAt)
            .Take(MaxPublishesPerTick)
            .Select(s => new { s.CorrelationId })
            .ToListAsync(ct);

        if (stuck.Count == 0) return;

        _logger.LogInformation(
            "RefundTimeoutWatcher: publishing RefundTimedOut for {Count} stuck saga(s)",
            stuck.Count);

        foreach (var saga in stuck)
        {
            try
            {
                await publishEndpoint.Publish(
                    new RefundTimedOutEvent
                    {
                        RefundId = saga.CorrelationId
                    },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RefundTimeoutWatcher: failed to publish for saga {SagaId}; will retry next tick",
                    saga.CorrelationId);
            }
        }
    }
}
