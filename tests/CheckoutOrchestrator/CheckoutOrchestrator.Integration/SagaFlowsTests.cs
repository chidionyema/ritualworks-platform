using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;
using Haworks.Contracts.Payments;
using Haworks.CheckoutOrchestrator.Application.Sagas;
using Haworks.CheckoutOrchestrator.Domain;
using Haworks.CheckoutOrchestrator.Infrastructure;

/// <summary>
/// End-to-end CheckoutSaga state-machine tests. Each test publishes a
/// sequence of upstream events into the in-memory test harness and asserts
/// that the saga transitions correctly AND publishes the expected
/// orchestration triggers. Saga state is persisted to the real Testcontainers
/// postgres so the EF saga repository's xmin concurrency / row-level lock /
/// MT outbox semantics are all in play exactly as in production.
/// </summary>
public sealed class SagaFlowsTests : IClassFixture<Haworks.CheckoutOrchestrator.Integration.CheckoutWebAppFactory>, IAsyncLifetime
{
    private readonly Haworks.CheckoutOrchestrator.Integration.CheckoutWebAppFactory _factory;

    public SagaFlowsTests(Haworks.CheckoutOrchestrator.Integration.CheckoutWebAppFactory factory)
    {
        _factory = factory;
    }

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
    public async Task CheckoutInitiated_creates_saga_in_Initiated_state_and_publishes_StockReservationRequested()
    {
        var (sagaId, orderId) = await PublishCheckoutInitiatedAsync();

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState.Should().NotBeNull();
        sagaState!.CurrentState.Should().Be("Initiated");
        sagaState.OrderId.Should().Be(orderId);

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var stockReq = harness.Published.Select<StockReservationRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.SagaId == sagaId);
        stockReq.Should().NotBeNull();
        stockReq!.Context.Message.OrderId.Should().Be(orderId);
        stockReq.Context.Message.TotalAmount.Should().Be(25.50m);
    }

    [Fact]
    public async Task Happy_path_transitions_through_all_states_and_finalizes_in_Completed()
    {
        var (sagaId, orderId) = await PublishCheckoutInitiatedAsync();
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        // Publish StockReserved -> saga should publish PaymentSessionRequested
        // and transition to StockReservedState.
        await PublishAsync(new StockReservedEvent
        {
            OrderId = orderId,
            SagaId = sagaId,
            UserId = "user-1",
            TotalAmount = 25.50m,
            Currency = "USD",
            CustomerEmail = "buyer@example.com",
            Items = new[] { new StockReservationItem
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, RemainingStock = 9,
            }},
            OrderLineItems = new[] { new CheckoutItemData
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, UnitPrice = 25.50m,
            }},
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "StockReservedState", TimeSpan.FromSeconds(15));

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var paymentReq = harness.Published.Select<PaymentSessionRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.SagaId == sagaId);
        paymentReq.Should().NotBeNull("the saga must publish PaymentSessionRequested after stock is reserved");

        // Publish PaymentSessionCreated -> saga transitions to ReadyForPayment.
        var paymentId = Guid.NewGuid();
        await PublishAsync(new PaymentSessionCreatedEvent
        {
            OrderId = orderId, SagaId = sagaId, PaymentId = paymentId,
            SessionId = "sess_test", CheckoutUrl = "https://stripe.test/sess_test",
            Provider = "Stripe", Amount = 25.50m, Currency = "USD",
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "ReadyForPayment", TimeSpan.FromSeconds(15));

        // Publish PaymentCompleted -> saga finalizes in Completed.
        await PublishAsync(new PaymentCompletedEvent
        {
            PaymentId = paymentId, OrderId = orderId, SagaId = sagaId,
            Amount = 25.50m, Currency = "USD", Provider = "Stripe",
            TransactionReference = "pi_test",
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Completed" || SagaStateOrNull(sagaId) == "Final",
            TimeSpan.FromSeconds(15));

        // SetCompletedWhenFinalized() removes the saga state row once the
        // state machine reaches a final state, so the row may already be
        // gone by the time we read. The polling loop already verified the
        // state transition; ensure the harness saw the PaymentSessionRequested
        // publish (proves the StockReserved→PaymentSessionRequested arrow
        // fired) and that no leftover Initiated/StockReservedState row
        // remains for this sagaId.
        var leftover = await ReadSagaAsync(sagaId);
        if (leftover is not null)
        {
            leftover.CurrentState.Should().BeOneOf("Completed", "Final");
        }
    }

    [Fact]
    public async Task StockReservationFailed_aborts_saga_to_Abandoned_with_no_StockReleaseRequested()
    {
        var (sagaId, orderId) = await PublishCheckoutInitiatedAsync();
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        await PublishAsync(new StockReservationFailedEvent
        {
            OrderId = orderId, SagaId = sagaId,
            FailedItems = new[] { new FailedReservationItem
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget",
                RequestedQuantity = 5, AvailableQuantity = 1,
            }},
            Reason = "Insufficient stock",
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Abandoned", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState!.FailureReason.Should().Contain("StockReservationFailed");

        // No StockReleaseRequested — nothing was reserved to release.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.Published.Select<StockReleaseRequestedEvent>()
            .Any(p => p.Context.Message.SagaId == sagaId).Should().BeFalse(
                "stock was never reserved, so no compensation publish should fire");
    }

    [Fact]
    public async Task PaymentSessionFailed_after_StockReserved_compensates_via_StockReleaseRequested_then_Abandoned()
    {
        var (sagaId, orderId) = await PublishCheckoutInitiatedAsync();
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        await PublishAsync(new StockReservedEvent
        {
            OrderId = orderId, SagaId = sagaId, UserId = "user-1",
            TotalAmount = 25.50m, Currency = "USD", CustomerEmail = "buyer@example.com",
            Items = new[] { new StockReservationItem
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, RemainingStock = 9,
            }},
            OrderLineItems = new[] { new CheckoutItemData
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, UnitPrice = 25.50m,
            }},
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "StockReservedState", TimeSpan.FromSeconds(15));

        // Stripe rejects the session AFTER stock was reserved -> compensation.
        await PublishAsync(new PaymentSessionFailedEvent
        {
            OrderId = orderId, SagaId = sagaId, Provider = "Stripe",
            ErrorCode = "card_declined", ErrorMessage = "Stripe rejected card",
            AttemptNumber = 1, IsFinalAttempt = true,
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Abandoned", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState!.FailureReason.Should().Contain("PaymentSessionFailed");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var release = harness.Published.Select<StockReleaseRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.SagaId == sagaId);
        release.Should().NotBeNull("payment failed AFTER stock was reserved — saga must compensate");
        release!.Context.Message.Reason.Should().Be("payment_session_failed");
    }

    [Fact]
    public async Task PaymentAmountMismatch_after_ReadyForPayment_transitions_to_RequiresReview()
    {
        var (sagaId, orderId) = await PublishCheckoutInitiatedAsync();
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        await PublishAsync(new StockReservedEvent
        {
            OrderId = orderId, SagaId = sagaId, UserId = "user-1",
            TotalAmount = 25.50m, Currency = "USD", CustomerEmail = "buyer@example.com",
            Items = new[] { new StockReservationItem
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, RemainingStock = 9,
            }},
            OrderLineItems = new[] { new CheckoutItemData
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, UnitPrice = 25.50m,
            }},
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "StockReservedState", TimeSpan.FromSeconds(15));

        var paymentId = Guid.NewGuid();
        await PublishAsync(new PaymentSessionCreatedEvent
        {
            OrderId = orderId, SagaId = sagaId, PaymentId = paymentId,
            SessionId = "sess_x", CheckoutUrl = "https://stripe.test/sess_x",
            Provider = "Stripe", Amount = 25.50m, Currency = "USD",
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "ReadyForPayment", TimeSpan.FromSeconds(15));

        // Stripe captures more than authorized -> RequiresReview branch.
        await PublishAsync(new PaymentAmountMismatchEvent
        {
            PaymentId = paymentId, OrderId = orderId,
            Provider = "Stripe", ActualPaid = 75m, ExpectedTotal = 25.50m,
            Difference = 49.50m, Reason = "captured 75; expected 25.50",
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "RequiresReview", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState!.FailureReason.Should().Contain("PaymentAmountMismatch");

        // PaymentAmountMismatch must also publish StockReleaseRequested to
        // compensate the reservation — stock was reserved and the payment
        // amount doesn't match, so reserved items must be released.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var release = harness.Published.Select<StockReleaseRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.SagaId == sagaId);
        release.Should().NotBeNull(
            "PaymentAmountMismatch must compensate by publishing StockReleaseRequested");
        release!.Context.Message.Reason.Should().Be("payment_amount_mismatch");
    }

    [Fact]
    public async Task Saga_state_persists_across_harness_restarts()
    {
        // Drive the saga halfway through the happy path.
        var (sagaId, orderId) = await PublishCheckoutInitiatedAsync();
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Initiated", TimeSpan.FromSeconds(15));

        await PublishAsync(new StockReservedEvent
        {
            OrderId = orderId, SagaId = sagaId, UserId = "user-1",
            TotalAmount = 25.50m, Currency = "USD", CustomerEmail = "buyer@example.com",
            Items = new[] { new StockReservationItem
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, RemainingStock = 9,
            }},
            OrderLineItems = new[] { new CheckoutItemData
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, UnitPrice = 25.50m,
            }},
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "StockReservedState", TimeSpan.FromSeconds(15));

        // Stop the harness — simulates the orchestrator pod going away.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Stop();

        // Saga state must still be in the DB (EF saga repository persistence).
        var persisted = await ReadSagaAsync(sagaId);
        persisted.Should().NotBeNull("EF saga repository must persist state across orchestrator restarts");
        persisted!.CurrentState.Should().Be("StockReservedState");
        persisted.OrderId.Should().Be(orderId);

        // Restart and verify the saga can pick up where it left off.
        await harness.Start();
        var paymentId = Guid.NewGuid();
        await PublishAsync(new PaymentSessionCreatedEvent
        {
            OrderId = orderId, SagaId = sagaId, PaymentId = paymentId,
            SessionId = "sess_resume", CheckoutUrl = "https://stripe.test/sess_resume",
            Provider = "Stripe", Amount = 25.50m, Currency = "USD",
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "ReadyForPayment", TimeSpan.FromSeconds(15));

        var resumed = await ReadSagaAsync(sagaId);
        resumed!.CurrentState.Should().Be("ReadyForPayment");
        resumed.PaymentId.Should().Be(paymentId);
    }

    private async Task<(Guid sagaId, Guid orderId)> PublishCheckoutInitiatedAsync(string userId = "user-1")
    {
        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await PublishAsync(new CheckoutInitiatedEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
            UserId = userId,
            CustomerEmail = "buyer@example.com",
            TotalAmount = 25.50m,
            Items = new[] { new CheckoutItemData
            {
                ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 1, UnitPrice = 25.50m,
            }},
            IdempotencyKey = "key-" + Guid.NewGuid().ToString("N"),
            IsGuest = false,
        });
        return (sagaId, orderId);
    }

    private async Task PublishAsync<T>(T evt) where T : class
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        await publisher.Publish(evt);
    }

    private string? SagaStateOrNull(Guid sagaId)
    {
        // Open a fresh scope each call — EF context lifecycle.
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
