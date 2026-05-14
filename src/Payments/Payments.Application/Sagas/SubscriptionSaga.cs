using MassTransit;
using Haworks.Payments.Domain;
using Haworks.Contracts;
using Haworks.Contracts.Payments;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Sagas;

public sealed record SubscriptionRenewalScheduled(Guid SubscriptionId);
public sealed record SubscriptionDunningScheduled(Guid SubscriptionId);

public sealed class SubscriptionSaga : MassTransitStateMachine<SubscriptionSagaState>
{
    public SubscriptionSaga(ILogger<SubscriptionSaga> logger)
    {
        InstanceState(s => s.CurrentState);

        Schedule(() => RenewalTimeoutSchedule, instance => instance.RenewalTimeoutTokenId, s =>
        {
            s.Received = r => r.CorrelateById(ctx => ctx.Message.SubscriptionId);
        });

        Schedule(() => DunningRetrySchedule, instance => instance.DunningRetryTokenId, s =>
        {
            s.Received = r => r.CorrelateById(ctx => ctx.Message.SubscriptionId);
        });

        Event(() => SubscriptionStarted, e => e.CorrelateBy((state, ctx) => state.ProviderSubscriptionId == ctx.Message.SubscriptionId));
        Event(() => SubscriptionRenewed, e => e.CorrelateBy((state, ctx) => state.ProviderSubscriptionId == ctx.Message.SubscriptionId));
        Event(() => RenewalRequested, e => e.CorrelateById(ctx => ctx.Message.SubscriptionId));
        Event(() => RenewalFailed, e => e.CorrelateById(ctx => ctx.Message.SubscriptionId));
        Event(() => PaymentRecovered, e => e.CorrelateById(ctx => ctx.Message.SubscriptionId));
        Event(() => SubscriptionCancelled, e => e.CorrelateBy((state, ctx) => state.ProviderSubscriptionId == ctx.Message.SubscriptionId));

        Initially(
            When(SubscriptionStarted)
                .Then(ctx =>
                {
                    var msg = ctx.Message;
                    var saga = ctx.Saga;
                    saga.ProviderSubscriptionId = msg.SubscriptionId;
                    saga.UserId = msg.UserId;
                    saga.PlanId = msg.PlanId;
                    saga.PeriodEnd = msg.CurrentPeriodEnd;
                    saga.CreatedAt = DateTime.UtcNow;

                    logger.LogInformation("Subscription Saga started for {SubscriptionId}, Period End: {PeriodEnd}",
                        msg.SubscriptionId, msg.CurrentPeriodEnd);
                })
                .Schedule(RenewalTimeoutSchedule, ctx => new SubscriptionRenewalScheduled(ctx.Saga.CorrelationId),
                    ctx => GuardDelay(ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1))) // SS-04: guard negative delay
                .TransitionTo(Active));

        During(Active,
            When(RenewalRequested)
                .Then(ctx => logger.LogInformation("Renewal requested for {SubscriptionId}", ctx.Saga.ProviderSubscriptionId))
                .TransitionTo(Renewing),
            When(RenewalTimeoutSchedule.Received)
                .Then(ctx => logger.LogInformation("Renewal scheduled time reached for {SubscriptionId}", ctx.Saga.ProviderSubscriptionId))
                .PublishAsync(ctx => ctx.Init<SubscriptionRenewalRequestedEvent>(new SubscriptionRenewalRequestedEvent
                {
                    SubscriptionId = ctx.Saga.CorrelationId,
                    ProviderSubscriptionId = ctx.Saga.ProviderSubscriptionId
                }))
                .TransitionTo(Renewing),
            When(RenewalFailed) // Handle out-of-sync failures
                .Then(ctx =>
                {
                    ctx.Saga.RetryCount++;
                    logger.LogWarning("Unexpected renewal failure in Active state for {SubscriptionId}. Attempt {RetryCount}",
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.RetryCount);
                })
                .PublishAsync(ctx => ctx.Init<SubscriptionGracePeriodStartedEvent>(new SubscriptionGracePeriodStartedEvent
                {
                    SubscriptionId = ctx.Saga.CorrelationId,
                    ExpiresAt = DateTime.UtcNow.AddDays(7)
                }))
                .TransitionTo(GracePeriod)
                .Schedule(DunningRetrySchedule, ctx => new SubscriptionDunningScheduled(ctx.Saga.CorrelationId),
                    ctx => TimeSpan.FromDays(2)));

        During(Renewing,
            When(SubscriptionRenewed)
                .Then(ctx =>
                {
                    ctx.Saga.PeriodEnd = ctx.Message.NewPeriodEnd;
                    ctx.Saga.RetryCount = 0;
                    logger.LogInformation("Subscription {SubscriptionId} successfully renewed until {PeriodEnd}",
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.PeriodEnd);
                })
                .Unschedule(RenewalTimeoutSchedule)
                .Schedule(RenewalTimeoutSchedule, ctx => new SubscriptionRenewalScheduled(ctx.Saga.CorrelationId),
                    ctx => GuardDelay(ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1))) // SS-04
                .TransitionTo(Active),
            When(RenewalFailed)
                .Then(ctx =>
                {
                    ctx.Saga.RetryCount++;
                    logger.LogWarning("Renewal failed for {SubscriptionId}. Entering Grace Period / Dunning. Attempt {RetryCount}",
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.RetryCount);
                })
                .PublishAsync(ctx => ctx.Init<SubscriptionGracePeriodStartedEvent>(new SubscriptionGracePeriodStartedEvent
                {
                    SubscriptionId = ctx.Saga.CorrelationId,
                    ExpiresAt = DateTime.UtcNow.AddDays(7)
                }))
                .TransitionTo(GracePeriod)
                .Schedule(DunningRetrySchedule, ctx => new SubscriptionDunningScheduled(ctx.Saga.CorrelationId),
                    ctx => TimeSpan.FromDays(2)));

        During(GracePeriod,
            When(SubscriptionRenewed)
                .Then(ctx =>
                {
                    ctx.Saga.PeriodEnd = ctx.Message.NewPeriodEnd;
                    ctx.Saga.RetryCount = 0;
                    logger.LogInformation("Subscription {SubscriptionId} recovered in Grace Period. New Period End: {PeriodEnd}",
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.PeriodEnd);
                })
                .Unschedule(DunningRetrySchedule)
                .Schedule(RenewalTimeoutSchedule, ctx => new SubscriptionRenewalScheduled(ctx.Saga.CorrelationId), // SS-06: re-schedule on recovery
                    ctx => GuardDelay(ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1)))
                .TransitionTo(Active),
            // SS-03: Handle PaymentRecovered in GracePeriod
            When(PaymentRecovered)
                .Then(ctx =>
                {
                    ctx.Saga.RetryCount = 0;
                    logger.LogInformation("Payment recovered for {SubscriptionId} during Grace Period", ctx.Saga.ProviderSubscriptionId);
                })
                .Unschedule(DunningRetrySchedule)
                .Schedule(RenewalTimeoutSchedule, ctx => new SubscriptionRenewalScheduled(ctx.Saga.CorrelationId),
                    ctx => GuardDelay(ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1)))
                .TransitionTo(Active),
            When(DunningRetrySchedule.Received)
                .Then(ctx => logger.LogInformation("Dunning retry triggered for {SubscriptionId}", ctx.Saga.ProviderSubscriptionId))
                .PublishAsync(ctx => ctx.Init<SubscriptionRenewalRequestedEvent>(new SubscriptionRenewalRequestedEvent
                {
                    SubscriptionId = ctx.Saga.CorrelationId,
                    ProviderSubscriptionId = ctx.Saga.ProviderSubscriptionId
                })),
            When(RenewalFailed)
                .Then(ctx =>
                {
                    ctx.Saga.RetryCount++;
                    logger.LogWarning("Dunning retry failed for {SubscriptionId}. Attempt {RetryCount}",
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.RetryCount);
                })
                .If(ctx => ctx.Saga.RetryCount <= 3,
                    binder => binder
                        .Schedule(DunningRetrySchedule, ctx => new SubscriptionDunningScheduled(ctx.Saga.CorrelationId),
                            ctx => TimeSpan.FromDays(2)))
                .If(ctx => ctx.Saga.RetryCount > 3,
                    binder => binder
                        .Then(ctx => logger.LogError("Dunning exhausted for {SubscriptionId}. Terminating access.", ctx.Saga.ProviderSubscriptionId))
                        .TransitionTo(Canceled)
                        .Finalize()));

        // SS-07: Handle cancellation in any state (not just Active)
        DuringAny(
            When(SubscriptionCancelled)
                .If(ctx => ctx.Saga.CurrentState != Canceled.Name,
                    binder => binder
                        .Then(ctx => logger.LogInformation("Subscription {SubscriptionId} cancelled", ctx.Saga.ProviderSubscriptionId))
                        .Unschedule(RenewalTimeoutSchedule)
                        .Unschedule(DunningRetrySchedule)
                        .TransitionTo(Canceled)
                        .Finalize()));

        SetCompletedWhenFinalized();
    }

    // SS-04: Guard against negative TimeSpan from past PeriodEnd
    private static TimeSpan GuardDelay(TimeSpan delay) =>
        delay < TimeSpan.Zero ? TimeSpan.Zero : delay;

    public State Active { get; private set; } = null!;
    public State Renewing { get; private set; } = null!;
    public State GracePeriod { get; private set; } = null!;
    public State Canceled { get; private set; } = null!;

    public Event<SubscriptionStartedEvent> SubscriptionStarted { get; private set; } = null!;
    public Event<SubscriptionRenewedEvent> SubscriptionRenewed { get; private set; } = null!;
    public Event<SubscriptionRenewalRequestedEvent> RenewalRequested { get; private set; } = null!;
    public Event<SubscriptionRenewalFailedEvent> RenewalFailed { get; private set; } = null!;
    public Event<SubscriptionPaymentRecoveredEvent> PaymentRecovered { get; private set; } = null!;
    public Event<SubscriptionCancelledEvent> SubscriptionCancelled { get; private set; } = null!;

    public Schedule<SubscriptionSagaState, SubscriptionRenewalScheduled> RenewalTimeoutSchedule { get; private set; } = null!;
    public Schedule<SubscriptionSagaState, SubscriptionDunningScheduled> DunningRetrySchedule { get; private set; } = null!;
}
