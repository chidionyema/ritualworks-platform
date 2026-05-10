using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;
using Haworks.Contracts.Payments;
using Haworks.CheckoutOrchestrator.Domain;
using Haworks.CheckoutOrchestrator.Infrastructure;

namespace Haworks.CheckoutOrchestrator.Integration;

/// <summary>
/// Saga reaction to PaymentExpiredEvent. The event itself can come from
/// two sources -- the MT scheduler (broker delayed-message-exchange) or
/// the polling PaymentExpiryWatcher fallback -- but the reaction is
/// the same regardless of source: publish StockReleaseRequested and
/// transition to Abandoned. These tests publish the event directly into
/// the harness to exercise the reaction path without depending on the
/// scheduler infrastructure (which can't be exercised under in-memory
/// test transport without time travel).
/// </summary>
public sealed class PaymentExpiryTests : IClassFixture<CheckoutWebAppFactory>, IAsyncLifetime
{
    private readonly CheckoutWebAppFactory _factory;

    public PaymentExpiryTests(CheckoutWebAppFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PaymentExpired_in_StockReservedState_compensates_via_StockReleaseRequested_then_Abandoned()
    {
        var (sagaId, orderId, productId) = await DriveSagaToStockReservedAsync();

        // The expiry tick fires while the saga is still waiting for a
        // PaymentSessionCreatedEvent. Same compensation path as a
        // PaymentSessionFailed in StockReserved.
        await PublishAsync(new PaymentExpiredEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Abandoned", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState!.FailureReason.Should().Contain("PaymentExpired");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var release = harness.Published.Select<StockReleaseRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.SagaId == sagaId);
        release.Should().NotBeNull(
            "stock was reserved before expiry — saga must compensate to release inventory");
        release!.Context.Message.Reason.Should().Be("payment_expired");
        release.Context.Message.Items.Should().ContainSingle(i => i.ProductId == productId);
    }

    [Fact]
    public async Task PaymentExpired_in_ReadyForPayment_also_compensates_then_Abandoned()
    {
        // Customer abandoned the Stripe/PayPal session after the URL
        // was created -- saga is in ReadyForPayment, stock is still
        // reserved, no PaymentCompleted will ever arrive. The expiry
        // tick is the only thing that frees the inventory.
        var (sagaId, orderId, productId) = await DriveSagaToStockReservedAsync();
        var paymentId = Guid.NewGuid();
        await PublishAsync(new PaymentSessionCreatedEvent
        {
            OrderId = orderId, SagaId = sagaId, PaymentId = paymentId,
            SessionId = "sess_test", CheckoutUrl = "https://stripe.test/sess_test",
            Provider = "Stripe", Amount = 25.50m, Currency = "USD",
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "ReadyForPayment", TimeSpan.FromSeconds(15));

        await PublishAsync(new PaymentExpiredEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Abandoned", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState!.FailureReason.Should().Contain("PaymentExpired");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var release = harness.Published.Select<StockReleaseRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.SagaId == sagaId);
        release.Should().NotBeNull(
            "saga reached ReadyForPayment with reserved stock — expiry must release it");
        release!.Context.Message.Reason.Should().Be("payment_expired");
    }

    [Fact]
    public async Task PaymentExpired_after_saga_already_Abandoned_is_silently_discarded()
    {
        // Race: scheduler fires expiry, saga compensates and lands in
        // Abandoned. Then the polling watcher fires too (or vice versa)
        // a second later. The duplicate event hits a saga in a final
        // state. The DuringAny block in CheckoutSaga doesn't include
        // PaymentExpiry — MT's default is to log and discard. This test
        // proves no second StockReleaseRequested is published, which
        // would be a double-compensation bug.
        var (sagaId, orderId, _) = await DriveSagaToStockReservedAsync();

        await PublishAsync(new PaymentExpiredEvent { SagaId = sagaId, OrderId = orderId });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Abandoned", TimeSpan.FromSeconds(15));

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var releaseCountBefore = harness.Published.Select<StockReleaseRequestedEvent>()
            .Count(p => p.Context.Message.SagaId == sagaId);

        // Second expiry firing AFTER the saga already finalized.
        await PublishAsync(new PaymentExpiredEvent { SagaId = sagaId, OrderId = orderId });
        // Give MT a moment to process the duplicate (should no-op).
        await Task.Delay(TimeSpan.FromSeconds(2));

        var releaseCountAfter = harness.Published.Select<StockReleaseRequestedEvent>()
            .Count(p => p.Context.Message.SagaId == sagaId);

        releaseCountAfter.Should().Be(releaseCountBefore,
            "a duplicate PaymentExpired must not trigger a second StockReleaseRequested");
    }

    private async Task<(Guid sagaId, Guid orderId, Guid productId)> DriveSagaToStockReservedAsync()
    {
        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        await PublishAsync(new CheckoutInitiatedEvent
        {
            SagaId = sagaId, OrderId = orderId, UserId = "user-1",
            CustomerEmail = "buyer@example.com", TotalAmount = 25.50m,
            Items = new[] { new CheckoutItemData
            {
                ProductId = productId, ProductName = "Widget", Quantity = 1, UnitPrice = 25.50m,
            }},
            IdempotencyKey = "key-" + Guid.NewGuid().ToString("N"),
            IsGuest = false,
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        await PublishAsync(new StockReservedEvent
        {
            OrderId = orderId, SagaId = sagaId, UserId = "user-1",
            TotalAmount = 25.50m, Currency = "USD", CustomerEmail = "buyer@example.com",
            Items = new[] { new StockReservationItem
            {
                ProductId = productId, ProductName = "Widget", Quantity = 1, RemainingStock = 9,
            }},
            OrderLineItems = new[] { new CheckoutItemData
            {
                ProductId = productId, ProductName = "Widget", Quantity = 1, UnitPrice = 25.50m,
            }},
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "StockReservedState", TimeSpan.FromSeconds(15));

        return (sagaId, orderId, productId);
    }

    private async Task PublishAsync<T>(T evt) where T : class
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        await publisher.Publish(evt);
    }

    private string? SagaStateOrNull(Guid sagaId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        return db.CheckoutSagas.AsNoTracking()
            .Where(s => s.CorrelationId == sagaId)
            .Select(s => s.CurrentState)
            .FirstOrDefault();
    }

    private async Task<CheckoutSagaState?> ReadSagaAsync(Guid sagaId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        return await db.CheckoutSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == sagaId);
    }

    private static async Task PollUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(250);
        }
    }
}
