using FluentAssertions;
using Haworks.Contracts.Catalog;
using PactNet;
using Xunit;
using Xunit.Abstractions;

namespace Haworks.Catalog.Contract;

/// <summary>
/// CONSUMER-SIDE Pact contract for <see cref="StockReservedEvent"/>.
///
/// This test plays the role of "any service that consumes catalog-svc's
/// StockReservedEvent" — the live consumer is payments-svc'
/// PaymentSessionConsumer (Phase 4) which uses every field on the event to
/// build the gateway session without re-querying foreign repositories.
///
/// PactNet writes the expectation to a JSON pact file; CI publishes it to
/// the Pact Broker so future provider changes can be caught with
/// <c>pact-broker can-i-deploy</c> before catalog-svc deploys a breaking
/// schema change.
///
/// Per ADR-0009 (DB-per-service): cross-context events are the boundary
/// API. They MUST carry every field consumers need; this contract pins
/// that surface.
/// </summary>
public sealed class StockReservedConsumerTests
{
    private readonly IMessagePactBuilderV4 _messagePact;

    public StockReservedConsumerTests(ITestOutputHelper output)
    {
        var config = new PactConfig
        {
            PactDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "pacts"),
            DefaultJsonSettings = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            },
        };

        _messagePact = Pact.V4("ConsumerOfCatalog", "catalog-svc", config).WithMessageInteractions();
    }

    [Fact]
    public Task StockReservedEvent_carries_order_saga_user_money_items_and_line_items()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var sagaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var productId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        return _messagePact
            .ExpectsToReceive("a StockReservedEvent for a 1-line order")
            .Given("an in-flight saga for orderId 11111111-... reserved 3 units of productId 33333333-...")
            .WithJsonContent(new
            {
                eventId        = "00000000-0000-0000-0000-000000000000",
                occurredAt     = "2026-05-03T12:00:00Z",
                orderId        = orderId.ToString(),
                sagaId         = sagaId.ToString(),
                userId         = "user-123",
                totalAmountCents = 3000,
                currency       = "USD",
                customerEmail  = "buyer@example.com",
                idempotencyKey = "key-abc",
                items = new[]
                {
                    new
                    {
                        productId      = productId.ToString(),
                        productName    = "Widget",
                        quantity       = 3,
                        remainingStock = 7,
                    }
                },
                orderLineItems = new[]
                {
                    new
                    {
                        productId   = productId.ToString(),
                        productName = "Widget",
                        quantity    = 3,
                        unitPriceCents = 1000,
                    }
                },
            })
            .VerifyAsync<StockReservedEvent>(evt =>
            {
                evt.Should().NotBeNull();
                evt.OrderId.Should().Be(orderId);
                evt.SagaId.Should().Be(sagaId);
                evt.UserId.Should().Be("user-123");
                evt.TotalAmountCents.Should().Be(3000);
                evt.Currency.Should().Be("USD");
                evt.CustomerEmail.Should().Be("buyer@example.com");
                evt.IdempotencyKey.Should().Be("key-abc");

                evt.Items.Should().ContainSingle();
                var item = evt.Items[0];
                item.ProductId.Should().Be(productId);
                item.ProductName.Should().Be("Widget");
                item.Quantity.Should().Be(3);
                item.RemainingStock.Should().Be(7);

                evt.OrderLineItems.Should().ContainSingle();
                var lineItem = evt.OrderLineItems[0];
                lineItem.ProductId.Should().Be(productId);
                lineItem.UnitPriceCents.Should().Be(1000);
                lineItem.Quantity.Should().Be(3);

                return Task.CompletedTask;
            });
    }
}
