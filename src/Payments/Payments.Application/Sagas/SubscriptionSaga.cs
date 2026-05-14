using MassTransit;
using Haworks.Payments.Domain;
using Haworks.Contracts.Payments;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Sagas;

public sealed class SubscriptionSaga : MassTransitStateMachine<SubscriptionSagaState>
{
    public SubscriptionSaga(ILogger<SubscriptionSaga> logger)
    {
        InstanceState(s => s.CurrentState);

        Schedule(() => RenewalTimeoutSchedule, instance => instance.RenewalTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromMinutes(1); // For demo, real would be much longer
            s.Received = r => r.CorrelateById(ctx => ctx.Message.SubscriptionId);
        });

        Schedule(() => DunningRetrySchedule, instance => instance.DunningRetryTokenId, s =>
        {
            s.Received = r => r.CorrelateById(ctx => ctx.Message.SubscriptionId);
        });

        Event(() => SubscriptionStarted, e => e.CorrelateBy((state, ctx) => state.ProviderSubscriptionId == ctx.Message.SubscriptionId));
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
                .Schedule(RenewalTimeoutSchedule, ctx => ctx.Init<SubscriptionRenewalRequestedEvent>(new SubscriptionRenewalRequestedEvent
                {
                    SubscriptionId = ctx.Saga.CorrelationId,
                    ProviderSubscriptionId = ctx.Saga.ProviderSubscriptionId
                }), ctx => ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1)) // Schedule 1 day before end
                .TransitionTo(Active));

        During(Active,
            When(RenewalRequested)
                .Then(ctx => logger.LogInformation("Renewal requested for {SubscriptionId}", ctx.Saga.ProviderSubscriptionId))
                .TransitionTo(Renewing),
            When(SubscriptionCancelled)
                .Then(ctx => logger.LogInformation("Subscription {SubscriptionId} cancelled by user", ctx.Saga.ProviderSubscriptionId))
                .TransitionTo(Canceled)
                .Finalize());

        During(Renewing,
            When(SubscriptionStarted) // Actually RenewalSucceeded would be better, but we reuse SubscriptionStarted or a specific event
                .Then(ctx =>
                {
                    ctx.Saga.PeriodEnd = ctx.Message.CurrentPeriodEnd;
                    ctx.Saga.RetryCount = 0;
                    logger.LogInformation("Subscription {SubscriptionId} successfully renewed until {PeriodEnd}", 
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.PeriodEnd);
                })
                .Unschedule(RenewalTimeoutSchedule)
                .Schedule(RenewalTimeoutSchedule, ctx => ctx.Init<SubscriptionRenewalRequestedEvent>(new SubscriptionRenewalRequestedEvent
                {
                    SubscriptionId = ctx.Saga.CorrelationId,
                    ProviderSubscriptionId = ctx.Saga.ProviderSubscriptionId
                }), ctx => ctx.Saga.PeriodEnd - DateTime.UtcNow - TimeSpan.FromDays(1))
                .TransitionTo(Active),
            When(RenewalFailed)
                .Then(ctx =>
                {
                    ctx.Saga.RetryCount++;
                    logger.LogWarning("Renewal failed for {SubscriptionId}. Attempt {RetryCount}", 
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.RetryCount);
                })
                .If(ctx => ctx.Saga.RetryCount <= 3,
                    binder => binder
                        .TransitionTo(Dunning)
                        .Schedule(DunningRetrySchedule, ctx => ctx.Init<SubscriptionRenewalRequestedEvent>(new SubscriptionRenewalRequestedEvent
                        {
                            SubscriptionId = ctx.Saga.CorrelationId,
                            ProviderSubscriptionId = ctx.Saga.ProviderSubscriptionId
                        }), ctx => TimeSpan.FromDays(ctx.Saga.RetryCount * 2))) // 2, 4, 6 days
                .If(ctx => ctx.Saga.RetryCount > 3,
                    binder => binder
                        .Then(ctx => logger.LogError("Dunning exhausted for {SubscriptionId}. Cancelling.", ctx.Saga.ProviderSubscriptionId))
                        .TransitionTo(Canceled)
                        .Finalize()));

        During(Dunning,
            When(PaymentRecovered)
                .Then(ctx =>
                {
                    ctx.Saga.RetryCount = 0;
                    logger.LogInformation("Payment recovered for {SubscriptionId}", ctx.Saga.ProviderSubscriptionId);
                })
                .Unschedule(DunningRetrySchedule)
                .TransitionTo(Active),
            When(RenewalFailed) // From scheduled retry
                .Then(ctx =>
                {
                    ctx.Saga.RetryCount++;
                    logger.LogWarning("Dunning retry failed for {SubscriptionId}. Attempt {RetryCount}", 
                        ctx.Saga.ProviderSubscriptionId, ctx.Saga.RetryCount);
                })
                .If(ctx => ctx.Saga.RetryCount <= 3,
                    binder => binder
                        .Schedule(DunningRetrySchedule, ctx => ctx.Init<SubscriptionRenewalRequestedEvent>(new SubscriptionRenewalRequestedEvent
                        {
                            SubscriptionId = ctx.Saga.CorrelationId,
                            ProviderSubscriptionId = ctx.Saga.ProviderSubscriptionId
                        }), ctx => TimeSpan.FromDays(ctx.Saga.RetryCount * 2)))
                .If(ctx => ctx.Saga.RetryCount > 3,
                    binder => binder
                        .TransitionTo(Canceled)
                        .Finalize()));

        SetCompletedWhenFinalized();
    }

    public State Active { get; private set; } = null!;
    public State Renewing { get; private set; } = null!;
    public State Dunning { get; private set; } = null!;
    public State Canceled { get; private set; } = null!;

    public Event<SubscriptionStartedEvent> SubscriptionStarted { get; private set; } = null!;
    public Event<SubscriptionRenewalRequestedEvent> RenewalRequested { get; private set; } = null!;
    public Event<SubscriptionRenewalFailedEvent> RenewalFailed { get; private set; } = null!;
    public Event<SubscriptionPaymentRecoveredEvent> PaymentRecovered { get; private set; } = null!;
    public Event<SubscriptionCancelledEvent> SubscriptionCancelled { get; private set; } = null!;

    public Schedule<SubscriptionSagaState, SubscriptionRenewalRequestedEvent> RenewalTimeoutSchedule { get; private set; } = null!;
    public Schedule<SubscriptionSagaState, SubscriptionRenewalRequestedEvent> DunningRetrySchedule { get; private set; } = null!;
}
