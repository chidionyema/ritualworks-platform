using FluentAssertions;
using Haworks.Catalog.Application.Consumers;
using Haworks.Contracts.Catalog;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Haworks.Catalog.Unit.Consumers;

/// <summary>
/// End-to-end retry behavior for the stock-release compensation path.
/// Wires StockReleaseRequestedConsumer with a forced-fail
/// IProductRepository into a MassTransit InMemory test harness, plus
/// the StockReleaseFaultConsumer, then publishes a single
/// StockReleaseRequestedEvent. Verifies:
///
///   1. The consumer is invoked multiple times (immediate retry layer
///      from StockReleaseRequestedConsumerDefinition).
///   2. After retries are exhausted MassTransit publishes a
///      Fault&lt;StockReleaseRequestedEvent&gt;.
///   3. StockReleaseFaultConsumer consumes the fault.
///
/// Delayed redelivery (1m/5m/15m/30m/1h) is NOT exercised here -- it
/// requires the broker scheduler infrastructure that the in-memory
/// transport doesn't simulate. The immediate retry layer is what
/// matters for the "transient blip" failure mode and that's what's
/// asserted.
/// </summary>
public sealed class StockReleaseRetryHarnessTests
{
    [Fact]
    public async Task Forced_failure_retries_and_finally_publishes_Fault()
    {
        // Forced-fail repository that throws every time. The real
        // consumer holds IProductRepository + IDomainEventPublisher;
        // both get stubbed so we control the failure mode.
        var products = new Mock<Haworks.Catalog.Domain.Interfaces.IProductRepository>();
        products.Setup(p => p.GetByIdTrackedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("simulated postgres deadlock"));

        var publisher = Mock.Of<Haworks.BuildingBlocks.Messaging.IDomainEventPublisher>();

        var services = new ServiceCollection();
        services.AddSingleton(products.Object);
        services.AddSingleton(publisher);
        services.AddSingleton<StockReleaseRequestedConsumer>();
        services.AddSingleton<StockReleaseFaultConsumer>();
        services.AddLogging();

        services.AddMassTransitTestHarness(mt =>
        {
            // Register the real consumer + the real fault consumer, but
            // attach a TEST consumer-definition that exercises only the
            // immediate-retry layer (no delayed redelivery — the in-memory
            // transport doesn't have a scheduler infrastructure).
            mt.AddConsumer<StockReleaseRequestedConsumer, RetryOnlyDefinition>();
            mt.AddConsumer<StockReleaseFaultConsumer>();
        });

        await using var provider = services.BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(new StockReleaseRequestedEvent
            {
                OrderId = Guid.NewGuid(),
                SagaId = Guid.NewGuid(),
                Reason = "test_compensation",
                Items = new[]
                {
                    new StockReservationItem { ProductId = Guid.NewGuid(), ProductName = "W", Quantity = 1, RemainingStock = 0 },
                },
            });

            // After retries exhaust, MT publishes Fault<StockReleaseRequestedEvent>
            // and the fault consumer picks it up. Wait on the fault publish so
            // we know the retry chain has completed before counting attempts.
            (await harness.Published.Any<Fault<StockReleaseRequestedEvent>>())
                .Should().BeTrue("MassTransit must publish a Fault once retries are exhausted");

            (await harness.GetConsumerHarness<StockReleaseFaultConsumer>()
                .Consumed.Any<Fault<StockReleaseRequestedEvent>>())
                .Should().BeTrue("the fault consumer must observe the Fault and ack it");

            // The forced-fail repository's GetByIdTrackedAsync is called once
            // per consumer attempt. UseMessageRetry(retryLimit:3) means
            // 1 original + 3 retries = 4 invocations. Use >=2 as the bound
            // to keep the test robust if MT version changes the retry count
            // semantics; the load-bearing claim is "more than once".
            products.Verify(
                p => p.GetByIdTrackedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(2),
                "the retry layer must invoke the consumer's repository call multiple times");
        }
        finally
        {
            await harness.Stop();
        }
    }

    /// <summary>
    /// Minimal consumer definition that only adds the immediate-retry
    /// layer from the production StockReleaseRequestedConsumerDefinition.
    /// Skips UseDelayedRedelivery (broker-scheduler-dependent) and
    /// UseEntityFrameworkOutbox (DB-dependent) so this test runs
    /// purely in-memory and finishes in seconds.
    /// </summary>
    private sealed class RetryOnlyDefinition : ConsumerDefinition<StockReleaseRequestedConsumer>
    {
        protected override void ConfigureConsumer(
            IReceiveEndpointConfigurator endpointConfigurator,
            IConsumerConfigurator<StockReleaseRequestedConsumer> consumerConfigurator,
            IRegistrationContext context)
        {
            endpointConfigurator.UseMessageRetry(r => r.Incremental(
                retryLimit: 3,
                initialInterval: TimeSpan.FromMilliseconds(50),
                intervalIncrement: TimeSpan.FromMilliseconds(50)));
        }
    }
}
