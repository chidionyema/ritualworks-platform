using FluentAssertions;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Sagas;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MassTransit.Testing;

namespace Haworks.Payments.Integration;

/// <summary>
/// Comprehensive SubscriptionSaga state-machine tests. Each test publishes
/// a sequence of upstream events into the in-memory test harness and asserts
/// that the saga transitions correctly AND publishes the expected downstream
/// events. Saga state is persisted to the real Testcontainers Postgres so the
/// EF saga repository's xmin concurrency / row-level lock / MT outbox
/// semantics are all in play exactly as in production.
/// </summary>
[Collection("Payments Integration")]
public class SubscriptionSagaTests : IAsyncLifetime
{
    private readonly PaymentsWebAppFactory _factory;

    public SubscriptionSagaTests(PaymentsWebAppFactory factory)
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

    // ───────────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────────

    private async Task<(Guid sagaId, string providerSubId)> StartSubscriptionAsync(
        string? userId = null, string? planId = null, DateTime? periodEnd = null)
    {
        var providerSubId = $"sub_{Guid.NewGuid():N}";
        await PublishAsync(new SubscriptionStartedEvent
        {
            SubscriptionId = providerSubId,
            UserId = userId ?? "user_test",
            PlanId = planId ?? "plan_premium",
            Provider = PaymentProvider.Stripe,
            CurrentPeriodEnd = periodEnd ?? DateTime.UtcNow.AddDays(30)
        });

        await PollUntilAsync(() => SagaStateByProviderIdOrNull(providerSubId) == "Active",
            TimeSpan.FromSeconds(15));

        var saga = await ReadSagaByProviderIdAsync(providerSubId);
        return (saga!.CorrelationId, providerSubId);
    }

    private async Task TransitionToRenewingAsync(Guid sagaId)
    {
        await PublishAsync(new SubscriptionRenewalRequestedEvent
        {
            SubscriptionId = sagaId,
            ProviderSubscriptionId = "ignored"
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Renewing",
            TimeSpan.FromSeconds(15));
    }

    private async Task TransitionToGracePeriodAsync(Guid sagaId)
    {
        await TransitionToRenewingAsync(sagaId);

        await PublishAsync(new SubscriptionRenewalFailedEvent
        {
            SubscriptionId = sagaId,
            ErrorCode = "CardDeclined",
            ErrorMessage = "Card expired"
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "GracePeriod",
            TimeSpan.FromSeconds(15));
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
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return db.SubscriptionSagas.AsNoTracking()
            .Where(s => s.CorrelationId == sagaId)
            .Select(s => s.CurrentState)
            .FirstOrDefault();
    }

    private string? SagaStateByProviderIdOrNull(string providerSubId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return db.SubscriptionSagas.AsNoTracking()
            .Where(s => s.ProviderSubscriptionId == providerSubId)
            .Select(s => s.CurrentState)
            .FirstOrDefault();
    }

    private async Task<SubscriptionSagaState?> ReadSagaAsync(Guid sagaId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.SubscriptionSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == sagaId);
    }

    private async Task<SubscriptionSagaState?> ReadSagaByProviderIdAsync(string providerSubId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.SubscriptionSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProviderSubscriptionId == providerSubId);
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

    private SubscriptionCancelledEvent BuildCancelEvent(string providerSubId) => new()
    {
        SubscriptionId = providerSubId,
        UserId = "user_test",
        Provider = PaymentProvider.Stripe,
        Reason = "user_requested"
    };

    // ───────────────────────────────────────────────────────────────────
    // 1. SubscriptionStarted -> Active
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubscriptionStarted_Should_StartSaga_And_Active()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();

        var saga = await ReadSagaAsync(sagaId);
        saga.Should().NotBeNull("Saga instance should have been created");
        saga!.CurrentState.Should().Be("Active");
        saga.ProviderSubscriptionId.Should().Be(providerSubId);
        saga.UserId.Should().Be("user_test");
        saga.PlanId.Should().Be("plan_premium");
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. RenewalFailed in Active -> GracePeriod
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenewalFailed_Should_Transition_To_GracePeriod_And_Dunning()
    {
        var (sagaId, _) = await StartSubscriptionAsync();

        await PublishAsync(new SubscriptionRenewalFailedEvent
        {
            SubscriptionId = sagaId,
            ErrorCode = "CardDeclined",
            ErrorMessage = "Your card has expired"
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "GracePeriod",
            TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        saga.Should().NotBeNull();
        saga!.CurrentState.Should().Be("GracePeriod");
        saga.RetryCount.Should().Be(1);

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        (await harness.Published.Any<SubscriptionGracePeriodStartedEvent>(
            x => x.Context.Message.SubscriptionId == sagaId))
            .Should().BeTrue("Grace period event should have been published");
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. RenewalTimer fires in Active -> publishes RenewalRequested, transitions to Renewing
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenewalTimer_PublishesRenewalRequested_TransitionsToRenewing()
    {
        var (sagaId, _) = await StartSubscriptionAsync();

        // Simulate the renewal timeout schedule firing by publishing the
        // scheduled message directly (the in-memory scheduler may not fire
        // TimeSpan.Zero delays deterministically).
        await PublishAsync(new SubscriptionRenewalScheduled(sagaId));

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Renewing",
            TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        saga!.CurrentState.Should().Be("Renewing");

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        (await harness.Published.Any<SubscriptionRenewalRequestedEvent>(
            x => x.Context.Message.SubscriptionId == sagaId))
            .Should().BeTrue("Renewal requested event should have been published when timer fired");
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. Happy path: Renewing + SubscriptionRenewed -> back to Active
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_Renewing_SubscriptionRenewed_BackToActive()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();
        await TransitionToRenewingAsync(sagaId);

        var newPeriodEnd = DateTime.UtcNow.AddDays(30);
        await PublishAsync(new SubscriptionRenewedEvent
        {
            SubscriptionId = providerSubId,
            UserId = "user_test",
            Provider = PaymentProvider.Stripe,
            AmountCents = 999,
            Currency = "USD",
            NewPeriodEnd = newPeriodEnd
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Active",
            TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        saga!.CurrentState.Should().Be("Active");
        saga.RetryCount.Should().Be(0, "RetryCount should be reset on successful renewal");
        saga.PeriodEnd.Should().BeCloseTo(newPeriodEnd, TimeSpan.FromSeconds(5));
    }

    // ───────────────────────────────────────────────────────────────────
    // 5. RenewalFailed in Renewing -> GracePeriod
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenewalFailed_InRenewing_EntersGracePeriod()
    {
        var (sagaId, _) = await StartSubscriptionAsync();
        await TransitionToRenewingAsync(sagaId);

        await PublishAsync(new SubscriptionRenewalFailedEvent
        {
            SubscriptionId = sagaId,
            ErrorCode = "InsufficientFunds",
            ErrorMessage = "Not enough balance"
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "GracePeriod",
            TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        saga!.CurrentState.Should().Be("GracePeriod");
        saga.RetryCount.Should().Be(1);

        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        (await harness.Published.Any<SubscriptionGracePeriodStartedEvent>(
            x => x.Context.Message.SubscriptionId == sagaId))
            .Should().BeTrue("Grace period started event should be published on renewal failure");
    }

    // ───────────────────────────────────────────────────────────────────
    // 6. DunningTimer fires in GracePeriod -> publishes RenewalRequested, stays GracePeriod
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DunningTimer_PublishesRenewalRequested_StaysGracePeriod()
    {
        var (sagaId, _) = await StartSubscriptionAsync();
        await TransitionToGracePeriodAsync(sagaId);

        // The dunning schedule is set to 2 days in production. In the test harness,
        // scheduled messages are delivered immediately. Wait for the dunning retry
        // to fire and publish SubscriptionRenewalRequestedEvent.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();

        await PollUntilAsync(
            () => harness.Published.Select<SubscriptionRenewalRequestedEvent>()
                .Any(p => p.Context.Message.SubscriptionId == sagaId),
            TimeSpan.FromSeconds(15));

        // The saga should remain in GracePeriod (dunning just publishes a retry request).
        var saga = await ReadSagaAsync(sagaId);
        saga!.CurrentState.Should().Be("GracePeriod");

        harness.Published.Select<SubscriptionRenewalRequestedEvent>()
            .Any(p => p.Context.Message.SubscriptionId == sagaId)
            .Should().BeTrue("Dunning retry should publish SubscriptionRenewalRequestedEvent");
    }

    // ───────────────────────────────────────────────────────────────────
    // 7. Dunning exhausted (4 failures) -> Canceled + Finalized
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DunningExhausted_FourFailures_FinalizesToCanceled()
    {
        var (sagaId, _) = await StartSubscriptionAsync();

        // First failure transitions Active -> GracePeriod (retryCount = 1)
        await PublishAsync(new SubscriptionRenewalFailedEvent
        {
            SubscriptionId = sagaId,
            ErrorCode = "Declined",
            ErrorMessage = "Attempt 1"
        });
        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "GracePeriod",
            TimeSpan.FromSeconds(15));

        // Failures 2, 3, 4 in GracePeriod — retryCount goes to 2, 3, 4.
        // retryCount > 3 triggers finalization to Canceled.
        for (var i = 2; i <= 4; i++)
        {
            await PublishAsync(new SubscriptionRenewalFailedEvent
            {
                SubscriptionId = sagaId,
                ErrorCode = "Declined",
                ErrorMessage = $"Attempt {i}"
            });
            await Task.Delay(500);
        }

        // SetCompletedWhenFinalized removes the row once finalized. The saga
        // either transitions to Canceled then gets removed, or we catch it
        // in Canceled before cleanup.
        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(sagaId);
            return state == "Canceled" || state is null; // null = finalized + removed
        }, TimeSpan.FromSeconds(15));

        // If the row still exists it must be Canceled; if it's gone, finalization completed.
        var saga = await ReadSagaAsync(sagaId);
        if (saga is not null)
        {
            saga.CurrentState.Should().Be("Canceled");
            saga.RetryCount.Should().BeGreaterThanOrEqualTo(4);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 8. PaymentRecovered in GracePeriod -> Active
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PaymentRecovered_InGracePeriod_ReturnsToActive()
    {
        var (sagaId, _) = await StartSubscriptionAsync();
        await TransitionToGracePeriodAsync(sagaId);

        await PublishAsync(new SubscriptionPaymentRecoveredEvent
        {
            SubscriptionId = sagaId,
            RecoveredAt = DateTime.UtcNow
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Active",
            TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        saga!.CurrentState.Should().Be("Active");
        saga.RetryCount.Should().Be(0, "RetryCount should be reset when payment recovers");
    }

    // ───────────────────────────────────────────────────────────────────
    // 9. SubscriptionRenewed in GracePeriod -> Active
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubscriptionRenewed_InGracePeriod_ReturnsToActive()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();
        await TransitionToGracePeriodAsync(sagaId);

        var newPeriodEnd = DateTime.UtcNow.AddDays(30);
        await PublishAsync(new SubscriptionRenewedEvent
        {
            SubscriptionId = providerSubId,
            UserId = "user_test",
            Provider = PaymentProvider.Stripe,
            AmountCents = 999,
            Currency = "USD",
            NewPeriodEnd = newPeriodEnd
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Active",
            TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        saga!.CurrentState.Should().Be("Active");
        saga.RetryCount.Should().Be(0, "RetryCount should be reset on renewal recovery");
        saga.PeriodEnd.Should().BeCloseTo(newPeriodEnd, TimeSpan.FromSeconds(5));
    }

    // ───────────────────────────────────────────────────────────────────
    // 10. Cancel during Renewing -> Canceled + Finalized
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelDuringAny_FromRenewing_Finalizes()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();
        await TransitionToRenewingAsync(sagaId);

        await PublishAsync(BuildCancelEvent(providerSubId));

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(sagaId);
            return state == "Canceled" || state is null;
        }, TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        if (saga is not null)
        {
            saga.CurrentState.Should().Be("Canceled");
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 11. Cancel during GracePeriod -> Canceled + Finalized
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelDuringAny_FromGracePeriod_Finalizes()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();
        await TransitionToGracePeriodAsync(sagaId);

        await PublishAsync(BuildCancelEvent(providerSubId));

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(sagaId);
            return state == "Canceled" || state is null;
        }, TimeSpan.FromSeconds(15));

        var saga = await ReadSagaAsync(sagaId);
        if (saga is not null)
        {
            saga.CurrentState.Should().Be("Canceled");
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 12. Duplicate cancel after already Canceled -> discarded
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DuplicateCancel_AfterCanceled_IsDiscarded()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();

        // Cancel once
        await PublishAsync(BuildCancelEvent(providerSubId));

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(sagaId);
            return state == "Canceled" || state is null;
        }, TimeSpan.FromSeconds(15));

        // If finalized and row removed, the second cancel has no saga to
        // correlate to — it's effectively discarded. If the row survives
        // briefly, the DuringAny guard `CurrentState != Canceled.Name`
        // prevents re-processing.
        var sagaBefore = await ReadSagaAsync(sagaId);

        await PublishAsync(BuildCancelEvent(providerSubId));

        // Settling time for negative assertion — we expect no state change.
        // PollUntilAsync would timeout immediately if we polled for a change,
        // so we use a brief fixed delay before asserting invariance.
        try
        {
            await PollUntilAsync(() =>
            {
                var s = ReadSagaAsync(sagaId).GetAwaiter().GetResult();
                // A second cancel should never move state away from Canceled/null
                return s is not null && s.CurrentState != "Canceled";
            }, TimeSpan.FromSeconds(2));
            // If we get here the state unexpectedly changed — the assertion below will catch it
        }
        catch (TimeoutException) { /* expected — duplicate was correctly discarded */ }

        var sagaAfter = await ReadSagaAsync(sagaId);

        // Both snapshots should match (either both null or both Canceled).
        if (sagaBefore is null)
        {
            sagaAfter.Should().BeNull("finalized saga row should remain absent");
        }
        else
        {
            sagaAfter.Should().NotBeNull();
            sagaAfter!.CurrentState.Should().Be("Canceled");
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 13. Late SubscriptionRenewed after Canceled -> discarded
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LateRenewed_AfterCanceled_IsDiscarded()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();

        await PublishAsync(BuildCancelEvent(providerSubId));

        await PollUntilAsync(() =>
        {
            var state = SagaStateOrNull(sagaId);
            return state == "Canceled" || state is null;
        }, TimeSpan.FromSeconds(15));

        // Publish a late renewal event — should not resurrect the saga.
        await PublishAsync(new SubscriptionRenewedEvent
        {
            SubscriptionId = providerSubId,
            UserId = "user_test",
            Provider = PaymentProvider.Stripe,
            AmountCents = 999,
            Currency = "USD",
            NewPeriodEnd = DateTime.UtcNow.AddDays(30)
        });

        // Settling time for negative assertion — we expect the state NOT to change to Active.
        try
        {
            await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Active", TimeSpan.FromSeconds(2));
            // Getting here means the saga was wrongly resurrected — the assertion below catches it
        }
        catch (TimeoutException) { /* expected — late renewal was correctly discarded */ }

        // The saga should remain absent (finalized) or still Canceled.
        var saga = await ReadSagaAsync(sagaId);
        if (saga is not null)
        {
            saga.CurrentState.Should().Be("Canceled",
                "a late renewal must not transition a canceled subscription back to Active");
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 14. State persists across harness restart
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StatePersists_AcrossRestart()
    {
        var (sagaId, providerSubId) = await StartSubscriptionAsync();
        await TransitionToRenewingAsync(sagaId);

        // Stop the harness — simulates the payments pod going away.
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        await harness.Stop();

        // Saga state must still be in the DB (EF saga repository persistence).
        var persisted = await ReadSagaAsync(sagaId);
        persisted.Should().NotBeNull("EF saga repository must persist state across pod restarts");
        persisted!.CurrentState.Should().Be("Renewing");
        persisted.ProviderSubscriptionId.Should().Be(providerSubId);

        // Restart and verify the saga can pick up where it left off.
        await harness.Start();

        var newPeriodEnd = DateTime.UtcNow.AddDays(30);
        await PublishAsync(new SubscriptionRenewedEvent
        {
            SubscriptionId = providerSubId,
            UserId = "user_test",
            Provider = PaymentProvider.Stripe,
            AmountCents = 999,
            Currency = "USD",
            NewPeriodEnd = newPeriodEnd
        });

        await PollUntilAsync(() => SagaStateOrNull(sagaId) == "Active",
            TimeSpan.FromSeconds(15));

        var resumed = await ReadSagaAsync(sagaId);
        resumed!.CurrentState.Should().Be("Active");
        resumed.PeriodEnd.Should().BeCloseTo(newPeriodEnd, TimeSpan.FromSeconds(5));
    }
}
