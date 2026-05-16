using FluentAssertions;
using Haworks.Contracts.Orders;
using PactNet;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Orders.Contract;

/// <summary>
/// CONSUMER-SIDE Pact contracts for the events orders-svc publishes:
///   • OrderCreatedEvent      (notification + analytics consumers)
///   • OrderCompletedEvent    (fulfillment + email consumers in Phase 5+)
///   • OrderAbandonedEvent    (catalog stock-release + recovery email)
/// </summary>
public sealed class OrderEventsConsumerTests
{
    private readonly IMessagePactBuilderV4 _pact;

    public OrderEventsConsumerTests(ITestOutputHelper output)
    {
        var config = new PactConfig
        {
            PactDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "pacts"),
            DefaultJsonSettings = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            },
        };

        _pact = Pact.V4("ConsumerOfOrders", "orders-svc", config).WithMessageInteractions();
    }

    [Fact]
    public Task OrderCreatedEvent_carries_orderId_customerId_total_email()
    {
        return _pact
            .ExpectsToReceive("an OrderCreatedEvent for a brand-new order")
            .Given("CreateOrderCommand just inserted order 11111111-...")
            .WithJsonContent(new
            {
                eventId       = "00000000-0000-0000-0000-000000000000",
                occurredAt    = "2026-05-03T12:00:00Z",
                orderId       = "11111111-1111-1111-1111-111111111111",
                customerId    = "22222222-2222-2222-2222-222222222222",
                totalAmount   = 25.50m,
                customerEmail = "buyer@example.com",
            })
            .VerifyAsync<OrderCreatedEvent>(evt =>
            {
                evt.OrderId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                evt.CustomerId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
                evt.TotalAmount.Should().Be(25.50m);
                evt.CustomerEmail.Should().Be("buyer@example.com");
                return Task.CompletedTask;
            });
    }

    [Fact]
    public Task OrderCompletedEvent_carries_orderId_customerId_total_email_completedAt_paymentId()
    {
        return _pact
            .ExpectsToReceive("an OrderCompletedEvent after PaymentCompletedConsumer transitioned the Order to Paid")
            .Given("payment 33333333-... completed for order 11111111-...")
            .WithJsonContent(new
            {
                eventId       = "00000000-0000-0000-0000-000000000000",
                occurredAt    = "2026-05-03T12:05:00Z",
                orderId       = "11111111-1111-1111-1111-111111111111",
                customerId    = "22222222-2222-2222-2222-222222222222",
                totalAmount   = 25.50m,
                customerEmail = "buyer@example.com",
                completedAt   = "2026-05-03T12:05:00Z",
                paymentId     = "33333333-3333-3333-3333-333333333333",
            })
            .VerifyAsync<OrderCompletedEvent>(evt =>
            {
                evt.OrderId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                evt.CustomerId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
                evt.PaymentId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
                evt.TotalAmount.Should().Be(25.50m);
                evt.CustomerEmail.Should().Be("buyer@example.com");
                return Task.CompletedTask;
            });
    }

    [Fact]
    public Task OrderAbandonedEvent_carries_order_saga_items_age_previousStatus_email()
    {
        return _pact
            .ExpectsToReceive("an OrderAbandonedEvent after StockReservationFailedConsumer transitioned the Order")
            .Given("stock reservation failed for order 11111111-...")
            .WithJsonContent(new
            {
                eventId    = "00000000-0000-0000-0000-000000000000",
                occurredAt = "2026-05-03T12:05:00Z",
                orderId    = "11111111-1111-1111-1111-111111111111",
                sagaId     = "44444444-4444-4444-4444-444444444444",
                items      = new[]
                {
                    new
                    {
                        productId      = "55555555-5555-5555-5555-555555555555",
                        productName    = "Widget",
                        quantity       = 2,
                        remainingStock = (int?)null,
                    }
                },
                ageAtAbandonment = "00:05:00",
                previousStatus   = "Created",
                customerEmail    = "buyer@example.com",
            })
            .VerifyAsync<OrderAbandonedEvent>(evt =>
            {
                evt.OrderId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                evt.SagaId.Should().Be(Guid.Parse("44444444-4444-4444-4444-444444444444"));
                evt.PreviousStatus.Should().Be("Created");
                evt.CustomerEmail.Should().Be("buyer@example.com");
                evt.Items.Should().ContainSingle();
                evt.Items.Single().ProductName.Should().Be("Widget");
                evt.Items.Single().Quantity.Should().Be(2);
                return Task.CompletedTask;
            });
    }
}
