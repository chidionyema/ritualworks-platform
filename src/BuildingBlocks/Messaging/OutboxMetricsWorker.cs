using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Messaging;

/// <summary>
/// Background worker that periodically measures undelivered outbox messages.
/// Exposes <c>masstransit.outbox.pending_count</c> as an ObservableGauge for
/// Prometheus scraping. Alerts fire when relay stalls and messages accumulate.
/// </summary>
public sealed class OutboxMetricsWorker<TContext> : BackgroundService
    where TContext : DbContext
{
    private static readonly Meter Meter = new("Haworks.MassTransit", "1.0.0");
    private long _pendingCount;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxMetricsWorker<TContext>> _logger;
    private readonly string _serviceName;

    public OutboxMetricsWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxMetricsWorker<TContext>> logger,
        IHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _serviceName = env.ApplicationName;

        Meter.CreateObservableGauge(
            "masstransit.outbox.pending_count",
            () => new Measurement<long>(_pendingCount,
                new KeyValuePair<string, object?>("service_name", _serviceName)),
            description: "Undelivered outbox messages awaiting relay to broker");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<TContext>();

                    _pendingCount = await db.Database
                        .SqlQueryRaw<int>(
                            "SELECT COUNT(*)::int AS \"Value\" FROM outbox_message WHERE delivered_at IS NULL")
                        .SingleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Outbox metrics query failed — will retry next cycle");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }
}
