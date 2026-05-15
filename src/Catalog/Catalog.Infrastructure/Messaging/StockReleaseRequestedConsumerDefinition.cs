using MassTransit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Application.Consumers;

namespace Haworks.Catalog.Infrastructure.Messaging;

/// <summary>
/// Hardened consumer definition for the compensation path. Stock release
/// is the LAST line of defense against orphaned reservations: if it
/// fails, payment has already succeeded and the customer's reserved
/// inventory is locked indefinitely until manual intervention.
///
/// Three layers of resilience on top of the standard catalog outbox
/// wiring:
///   1. Immediate retry — 3 attempts with 1s/3s/5s backoff. Catches
///      transient Postgres deadlocks, brief connectivity blips.
///   2. Delayed redelivery — 5 redeliveries at 1m/5m/15m/30m/1h.
///      Covers a temporary catalog-svc outage; broker holds the
///      message until the next interval.
///   3. After all redelivery attempts are exhausted the message goes
///      to the _error queue (fault topic). <see cref="StockReleaseFaultConsumer"/>
///      consumes the fault and logs CRITICAL with full context so
///      operators get an alert with the orderId + items needed to
///      manually unstick the reservation.
/// </summary>
public sealed class StockReleaseRequestedConsumerDefinition
    : BoundedContextConsumerDefinition<StockReleaseRequestedConsumer, CatalogDbContext>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StockReleaseRequestedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Layer 2: delayed redelivery (uses the broker's delayed-message
        // exchange; RabbitMQ has the rabbitmq_delayed_message_exchange
        // plugin enabled per fly.rabbitmq config).
        endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1)));

        // Layer 0+1: the standard catalog outbox + baseline immediate retry
        // wiring from BoundedContextConsumerDefinition.
        base.ConfigureConsumer(endpointConfigurator, consumerConfigurator, context);
    }
}
