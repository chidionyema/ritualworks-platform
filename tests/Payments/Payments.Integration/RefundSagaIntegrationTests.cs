using System.Net.Http.Json;
using FluentAssertions;
using Haworks.Contracts.Payments;
using Haworks.Payments.Api.Controllers;
using Haworks.Payments.Application.Queries.Refunds;
using Haworks.Payments.Application.Sagas;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.Logging;
using Moq;

namespace Haworks.Payments.Integration;

[Collection("Payments Integration")]
public class RefundSagaIntegrationTests : IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;
    private readonly HttpClient _client;

    public RefundSagaIntegrationTests(PaymentsWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
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
    public async Task CreateRefund_Should_StartSaga_And_ReachAwaitingProvider()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        var payment = Payment.Create(
            Guid.NewGuid(),
            "user_123",
            100.00m,
            0,
            "USD",
            PaymentProvider.Stripe,
            Guid.NewGuid());

        payment.MarkCompleted("pi_test_123", "card");

        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var request = new CreateRefundRequest(
            PaymentId: payment.Id,
            Amount: 50.00m,
            Currency: "USD",
            Reason: "Test refund",
            RequestedBy: "TestRunner"
        );

        // Act: Call API
        var response = await _client.PostAsJsonAsync("/api/refunds", request);
        response.EnsureSuccessStatusCode();
        var refundId = await response.Content.ReadFromJsonAsync<Guid>();

        // Wait for the API handler + harness pipeline to process
        await Task.Delay(3000);

        (await harness.Published.Any<RefundRequestedEvent>(x => x.Context.Message.RefundId == refundId))
            .Should().BeTrue("RefundRequestedEvent should have been published by the API handler");

        var sagaHarness = harness.GetSagaStateMachineHarness<RefundSaga, RefundSagaState>();

        (await sagaHarness.Consumed.Any<RefundRequestedEvent>(x => x.Context.Message.RefundId == refundId))
            .Should().BeTrue("Saga should have consumed RefundRequestedEvent");

        (await sagaHarness.Created.Any(x => x.CorrelationId == refundId))
            .Should().BeTrue("Saga instance should have been created");

        (await harness.Published.Any<ProviderRefundInitiationRequestedEvent>(x => x.Context.Message.RefundId == refundId))
            .Should().BeTrue("Saga should have published ProviderRefundInitiationRequestedEvent");
    }

    // ---------------------------------------------------------------
    // 1. Happy path: RefundRequested -> ProviderRefundInitiated ->
    //    ProviderRefundSucceeded -> Refunded (finalized)
    // ---------------------------------------------------------------
    [Fact]
    public async Task HappyPath_ProviderConfirms_ReachesRefunded()
    {
        var (refundId, orderId, paymentId) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.Published.Select<ProviderRefundInitiationRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId)
            .Should().NotBeNull("saga must publish ProviderRefundInitiationRequestedEvent on entry to Requested");

        // Provider acknowledges initiation -> AwaitingProviderConfirmation
        await PublishAsync(new ProviderRefundInitiatedEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_001",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "AwaitingProviderConfirmation", TimeSpan.FromSeconds(15));

        var awaitingState = await ReadSagaAsync(refundId);
        awaitingState.Should().NotBeNull();
        awaitingState!.ProviderRefundId.Should().Be("re_provider_001");

        // Provider confirms success -> Refunded (finalized, row may be removed)
        await PublishAsync(new ProviderRefundSucceededEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_001",
            AmountRefunded = 50.00m,
            CompletedAt = DateTime.UtcNow,
        });
        await PollUntilAsync(
            () => SagaStateOrNull(refundId) is "Refunded" or "Final" or null,
            TimeSpan.FromSeconds(15));

        // RefundCompletedEvent must have been published
        harness.Published.Select<RefundCompletedEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId)
            .Should().NotBeNull("saga must publish RefundCompletedEvent on successful refund");

        var completed = harness.Published.Select<RefundCompletedEvent>()
            .First(p => p.Context.Message.RefundId == refundId);
        completed.Context.Message.OrderId.Should().Be(orderId);
        completed.Context.Message.PaymentId.Should().Be(paymentId);
        completed.Context.Message.Amount.Should().Be(50.00m);
        completed.Context.Message.Currency.Should().Be("USD");
    }

    // ---------------------------------------------------------------
    // 2. Provider fails while saga is in Requested state
    // ---------------------------------------------------------------
    [Fact]
    public async Task ProviderFails_InRequested_TransitionsToRequiresReview()
    {
        var (refundId, orderId, _) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        await PublishAsync(new ProviderRefundFailedEvent
        {
            RefundId = refundId,
            ErrorCode = "provider_error",
            ErrorMessage = "Insufficient funds on provider account",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "RequiresReview", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(refundId);
        sagaState.Should().NotBeNull();
        sagaState!.CurrentState.Should().Be("RequiresReview");
        sagaState.FailureCategory.Should().Be(RefundFailureCategory.ProviderRefundFailed);
        sagaState.FailureDetail.Should().Contain("provider_error");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var failed = harness.Published.Select<RefundFailedEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId);
        failed.Should().NotBeNull("saga must publish RefundFailedEvent");
        failed!.Context.Message.FailureCategory.Should().Be("ProviderRefundFailed");
        failed.Context.Message.OrderId.Should().Be(orderId);
    }

    // ---------------------------------------------------------------
    // 3. Provider fails while saga is in AwaitingProviderConfirmation
    // ---------------------------------------------------------------
    [Fact]
    public async Task ProviderFails_InAwaitingConfirmation_TransitionsToRequiresReview()
    {
        var (refundId, orderId, _) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        // Move to AwaitingProviderConfirmation
        await PublishAsync(new ProviderRefundInitiatedEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_002",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "AwaitingProviderConfirmation", TimeSpan.FromSeconds(15));

        // Provider fails late
        await PublishAsync(new ProviderRefundFailedEvent
        {
            RefundId = refundId,
            ErrorCode = "charge_already_refunded",
            ErrorMessage = "Charge has already been refunded",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "RequiresReview", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(refundId);
        sagaState.Should().NotBeNull();
        sagaState!.CurrentState.Should().Be("RequiresReview");
        sagaState.FailureCategory.Should().Be(RefundFailureCategory.ProviderRefundFailed);
        sagaState.FailureDetail.Should().Contain("charge_already_refunded");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var failed = harness.Published.Select<RefundFailedEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId);
        failed.Should().NotBeNull("saga must publish RefundFailedEvent on late provider failure");
        failed!.Context.Message.OrderId.Should().Be(orderId);
    }

    // ---------------------------------------------------------------
    // 4. Timeout fires while in AwaitingProviderConfirmation
    // ---------------------------------------------------------------
    [Fact]
    public async Task Timeout_TransitionsToRequiresReview()
    {
        var (refundId, _, _) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        // Move to AwaitingProviderConfirmation
        await PublishAsync(new ProviderRefundInitiatedEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_003",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "AwaitingProviderConfirmation", TimeSpan.FromSeconds(15));

        // Simulate the scheduled timeout firing by publishing the event directly
        await PublishAsync(new RefundTimedOutEvent
        {
            RefundId = refundId,
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "RequiresReview", TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(refundId);
        sagaState.Should().NotBeNull();
        sagaState!.CurrentState.Should().Be("RequiresReview");
        sagaState.FailureCategory.Should().Be(RefundFailureCategory.RefundTimedOut);
        sagaState.FailureDetail.Should().Contain("24 hours");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var stalled = harness.Published.Select<RefundStalledEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId);
        stalled.Should().NotBeNull("saga must publish RefundStalledEvent on timeout");
        stalled!.Context.Message.HoursSinceRequest.Should().Be(24);
    }

    // ---------------------------------------------------------------
    // 5. Operator cancels from Requested state -> Cancelled (finalized)
    // ---------------------------------------------------------------
    [Fact]
    public async Task OperatorCancel_FromRequested_FinalizesToCancelled()
    {
        var (refundId, orderId, _) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        await PublishAsync(new RefundCancelledByOperatorEvent
        {
            RefundId = refundId,
        });
        await PollUntilAsync(
            () => SagaStateOrNull(refundId) is "Cancelled" or "Final" or null,
            TimeSpan.FromSeconds(15));

        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        // RefundCancelledEvent must be published
        var cancelled = harness.Published.Select<RefundCancelledEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId);
        cancelled.Should().NotBeNull("saga must publish RefundCancelledEvent");
        cancelled!.Context.Message.OrderId.Should().Be(orderId);
        cancelled.Context.Message.Reason.Should().Contain("operator");

        // No ProviderRefundCancellationRequested — saga was not yet at AwaitingProviderConfirmation
        harness.Published.Select<ProviderRefundCancellationRequestedEvent>()
            .Any(p => p.Context.Message.RefundId == refundId)
            .Should().BeFalse("no provider cancellation needed when cancelled from Requested state");
    }

    // ---------------------------------------------------------------
    // 6. Operator cancels from AwaitingProviderConfirmation ->
    //    publishes ProviderRefundCancellationRequested then Cancelled
    // ---------------------------------------------------------------
    [Fact]
    public async Task OperatorCancel_FromAwaitingConfirmation_PublishesProviderCancellation()
    {
        var (refundId, orderId, _) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        // Move to AwaitingProviderConfirmation
        await PublishAsync(new ProviderRefundInitiatedEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_004",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "AwaitingProviderConfirmation", TimeSpan.FromSeconds(15));

        // Operator cancels
        await PublishAsync(new RefundCancelledByOperatorEvent
        {
            RefundId = refundId,
        });
        await PollUntilAsync(
            () => SagaStateOrNull(refundId) is "Cancelled" or "Final" or null,
            TimeSpan.FromSeconds(15));

        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        // ProviderRefundCancellationRequested must be published (provider needs to know)
        var providerCancel = harness.Published.Select<ProviderRefundCancellationRequestedEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId);
        providerCancel.Should().NotBeNull(
            "saga must publish ProviderRefundCancellationRequestedEvent when cancelled from AwaitingProviderConfirmation");
        providerCancel!.Context.Message.ProviderRefundId.Should().Be("re_provider_004");

        // RefundCancelledEvent must also be published
        var cancelled = harness.Published.Select<RefundCancelledEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId);
        cancelled.Should().NotBeNull();
        cancelled!.Context.Message.OrderId.Should().Be(orderId);
    }

    // ---------------------------------------------------------------
    // 7. Duplicate ProviderRefundSucceeded after Refunded is discarded
    // ---------------------------------------------------------------
    [Fact]
    public async Task DuplicateProviderConfirm_AfterRefunded_IsDiscarded()
    {
        var (refundId, _, _) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        await PublishAsync(new ProviderRefundInitiatedEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_005",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "AwaitingProviderConfirmation", TimeSpan.FromSeconds(15));

        // First success -> Refunded
        await PublishAsync(new ProviderRefundSucceededEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_005",
            AmountRefunded = 50.00m,
            CompletedAt = DateTime.UtcNow,
        });
        await PollUntilAsync(
            () => SagaStateOrNull(refundId) is "Refunded" or "Final" or null,
            TimeSpan.FromSeconds(15));

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        var completedCountBefore = harness.Published.Select<RefundCompletedEvent>()
            .Count(p => p.Context.Message.RefundId == refundId);

        // Duplicate success — must be silently discarded (DuringAny guard)
        await PublishAsync(new ProviderRefundSucceededEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_005",
            AmountRefunded = 50.00m,
            CompletedAt = DateTime.UtcNow,
        });

        // Poll briefly so MT has a chance to (mis-)process the duplicate before we assert count is unchanged.
        // We expect the count to stay the same, so a TimeoutException here is the success case.
        try
        {
            await PollUntilAsync(
                () => harness.Published.Select<RefundCompletedEvent>()
                          .Count(p => p.Context.Message.RefundId == refundId) > completedCountBefore,
                TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException) { /* expected — duplicate was correctly discarded */ }

        // No additional RefundCompletedEvent should have been published
        var completedCountAfter = harness.Published.Select<RefundCompletedEvent>()
            .Count(p => p.Context.Message.RefundId == refundId);
        completedCountAfter.Should().Be(completedCountBefore,
            "duplicate ProviderRefundSucceeded on a finalized saga must not publish another RefundCompletedEvent");
    }

    // ---------------------------------------------------------------
    // 8. Saga state persists across harness restart
    // ---------------------------------------------------------------
    [Fact]
    public async Task StatePersists_AcrossRestart()
    {
        var (refundId, orderId, _) = await PublishRefundRequestedAsync();
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "Requested", TimeSpan.FromSeconds(15));

        // Move to AwaitingProviderConfirmation
        await PublishAsync(new ProviderRefundInitiatedEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_006",
        });
        await PollUntilAsync(() => SagaStateOrNull(refundId) == "AwaitingProviderConfirmation", TimeSpan.FromSeconds(15));

        // Stop harness — simulates pod restart
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Stop();

        // Saga state must still be in the DB (EF saga repository persistence)
        var persisted = await ReadSagaAsync(refundId);
        persisted.Should().NotBeNull("EF saga repository must persist state across restarts");
        persisted!.CurrentState.Should().Be("AwaitingProviderConfirmation");
        persisted.OrderId.Should().Be(orderId);
        persisted.ProviderRefundId.Should().Be("re_provider_006");

        // Restart and verify the saga can pick up where it left off
        await harness.Start();

        await PublishAsync(new ProviderRefundSucceededEvent
        {
            RefundId = refundId,
            ProviderRefundId = "re_provider_006",
            AmountRefunded = 50.00m,
            CompletedAt = DateTime.UtcNow,
        });
        await PollUntilAsync(
            () => SagaStateOrNull(refundId) is "Refunded" or "Final" or null,
            TimeSpan.FromSeconds(15));

        harness.Published.Select<RefundCompletedEvent>()
            .FirstOrDefault(p => p.Context.Message.RefundId == refundId)
            .Should().NotBeNull("saga must resume and complete after restart");
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private async Task<(Guid refundId, Guid orderId, Guid paymentId)> PublishRefundRequestedAsync()
    {
        var refundId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await PublishAsync(new RefundRequestedEvent
        {
            RefundId = refundId,
            OrderId = orderId,
            PaymentId = paymentId,
            Amount = 50.00m,
            Currency = "USD",
            Reason = "Integration test refund",
            Provider = "Stripe",
        });

        return (refundId, orderId, paymentId);
    }

    private async Task PublishAsync<T>(T evt) where T : class
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await publisher.Publish(evt);
    }

    private string? SagaStateOrNull(Guid refundId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return db.RefundSagas.AsNoTracking()
            .Where(s => s.CorrelationId == refundId)
            .Select(s => s.CurrentState)
            .FirstOrDefault();
    }

    private async Task<RefundSagaState?> ReadSagaAsync(Guid refundId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.RefundSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == refundId);
    }

    private static async Task PollUntilAsync(Func<bool> predicate, TimeSpan timeout, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"PollUntilAsync timed out after {timeout.TotalSeconds}s in {caller}");
    }
}
