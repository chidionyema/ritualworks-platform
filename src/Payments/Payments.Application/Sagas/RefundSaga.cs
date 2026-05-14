using MassTransit;
using Haworks.Payments.Domain;
using Haworks.Payments.Application.Telemetry;
using Haworks.Contracts.Payments;

namespace Haworks.Payments.Application.Sagas;

public sealed class RefundSaga : MassTransitStateMachine<RefundSagaState>
{
    public RefundSaga()
    {
        InstanceState(s => s.CurrentState);

        Schedule(
            () => RefundTimeoutSchedule,
            instance => instance.RefundTimeoutTokenId,
            s =>
            {
                s.Delay = TimeSpan.FromHours(24);
                s.Received = r => r.CorrelateById(ctx => ctx.Message.RefundId);
            });

        Event(() => RefundRequested, e => e.CorrelateById(ctx => ctx.Message.RefundId));
        Event(() => ProviderRefundInitiated, e => e.CorrelateById(ctx => ctx.Message.RefundId));
        Event(() => ProviderRefundSucceeded, e => e.CorrelateById(ctx => ctx.Message.RefundId));
        Event(() => ProviderRefundFailed, e => e.CorrelateById(ctx => ctx.Message.RefundId));
        Event(() => RefundCancelledByOperator, e => e.CorrelateById(ctx => ctx.Message.RefundId));

        Initially(
            When(RefundRequested)
                .Then(ctx =>
                {
                    var msg = ctx.Message;
                    var saga = ctx.Saga;
                    saga.OrderId = msg.OrderId;
                    saga.PaymentId = msg.PaymentId;
                    saga.RefundId = msg.RefundId;
                    saga.Amount = msg.Amount;
                    saga.Currency = msg.Currency;
                    saga.Reason = msg.Reason ?? "";
                    saga.Provider = msg.Provider ?? "Stripe";
                    saga.CreatedAt = DateTime.UtcNow;
                })
                .PublishAsync(ctx => ctx.Init<ProviderRefundInitiationRequestedEvent>(new ProviderRefundInitiationRequestedEvent
                {
                    RefundId = ctx.Saga.CorrelationId,
                    Provider = ctx.Saga.Provider, // RS-04: use provider from event, not hardcoded
                    PaymentId = ctx.Saga.PaymentId,
                    Amount = ctx.Saga.Amount,
                    Currency = ctx.Saga.Currency
                }))
                .Schedule(RefundTimeoutSchedule, ctx => ctx.Init<RefundTimedOutEvent>(new RefundTimedOutEvent
                {
                    RefundId = ctx.Saga.CorrelationId
                }))
                .TransitionTo(Requested));

        During(Requested,
            When(ProviderRefundInitiated)
                .Then(ctx =>
                {
                    ctx.Saga.ProviderRefundId = ctx.Message.ProviderRefundId;
                })
                .TransitionTo(AwaitingProviderConfirmation),
            When(ProviderRefundFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureCategory = RefundFailureCategory.ProviderRefundFailed;
                    ctx.Saga.FailureDetail = $"{ctx.Message.ErrorCode}: {ctx.Message.ErrorMessage}";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "provider_refund_failed");
                })
                .Unschedule(RefundTimeoutSchedule)
                .PublishAsync(ctx => ctx.Init<RefundFailedEvent>(new RefundFailedEvent
                {
                    RefundId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    FailureCategory = "ProviderRefundFailed",
                    FailureDetail = ctx.Saga.FailureDetail ?? "Unknown provider error"
                }))
                .TransitionTo(RequiresReview));

        During(AwaitingProviderConfirmation,
            When(ProviderRefundSucceeded)
                .Then(ctx =>
                {
                    // Optionally update amount refunded if it differs
                })
                .Unschedule(RefundTimeoutSchedule)
                .PublishAsync(ctx => ctx.Init<RefundCompletedEvent>(new RefundCompletedEvent
                {
                    RefundId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    PaymentId = ctx.Saga.PaymentId,
                    Amount = ctx.Saga.Amount,
                    Currency = ctx.Saga.Currency
                }))
                .TransitionTo(Refunded)
                .Finalize(),
            When(ProviderRefundFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureCategory = RefundFailureCategory.ProviderRefundFailed;
                    ctx.Saga.FailureDetail = $"{ctx.Message.ErrorCode}: {ctx.Message.ErrorMessage}";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "provider_refund_failed_late");
                })
                .Unschedule(RefundTimeoutSchedule)
                .PublishAsync(ctx => ctx.Init<RefundFailedEvent>(new RefundFailedEvent
                {
                    RefundId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    FailureCategory = "ProviderRefundFailed",
                    FailureDetail = ctx.Saga.FailureDetail ?? "Unknown provider confirmation error"
                }))
                .TransitionTo(RequiresReview),
            When(RefundTimeoutSchedule.Received)
                .Then(ctx =>
                {
                    ctx.Saga.FailureCategory = RefundFailureCategory.RefundTimedOut;
                    ctx.Saga.FailureDetail = "Provider did not confirm refund within 24 hours";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "refund_timeout");
                })
                .PublishAsync(ctx => ctx.Init<RefundStalledEvent>(new RefundStalledEvent
                {
                    RefundId = ctx.Saga.CorrelationId,
                    HoursSinceRequest = 24
                }))
                .TransitionTo(RequiresReview));

        DuringAny(
            When(RefundCancelledByOperator)
                .Then(ctx =>
                {
                    ctx.Saga.FailureCategory = RefundFailureCategory.CancelledByOperator;
                    ctx.Saga.FailureDetail = "Cancelled by operator";
                })
                .Unschedule(RefundTimeoutSchedule)
                .If(ctx => ctx.Saga.CurrentState == AwaitingProviderConfirmation.Name,
                    x => x.PublishAsync(ctx => ctx.Init<ProviderRefundCancellationRequestedEvent>(new ProviderRefundCancellationRequestedEvent
                    {
                        RefundId = ctx.Saga.CorrelationId,
                        ProviderRefundId = ctx.Saga.ProviderRefundId ?? ""
                    })))
                .PublishAsync(ctx => ctx.Init<RefundCancelledEvent>(new RefundCancelledEvent
                {
                    RefundId = ctx.Saga.CorrelationId,
                    OrderId = ctx.Saga.OrderId,
                    Reason = "Cancelled by operator"
                }))
                .TransitionTo(Cancelled)
                .Finalize());

        SetCompletedWhenFinalized();
    }

    public State Requested { get; private set; } = null!;
    public State AwaitingProviderConfirmation { get; private set; } = null!;
    public State Refunded { get; private set; } = null!;
    public State RequiresReview { get; private set; } = null!;
    public State Cancelled { get; private set; } = null!;

    public Event<RefundRequestedEvent> RefundRequested { get; private set; } = null!;
    public Event<ProviderRefundInitiatedEvent> ProviderRefundInitiated { get; private set; } = null!;
    public Event<ProviderRefundSucceededEvent> ProviderRefundSucceeded { get; private set; } = null!;
    public Event<ProviderRefundFailedEvent> ProviderRefundFailed { get; private set; } = null!;
    public Event<RefundCancelledByOperatorEvent> RefundCancelledByOperator { get; private set; } = null!;

    public Schedule<RefundSagaState, RefundTimedOutEvent> RefundTimeoutSchedule { get; private set; } = null!;

    private static void EmitCompensateSpan(Guid sagaId, Guid orderId, string reason)
    {
        using var activity = PaymentsActivities.Source.StartActivity("refund.saga.compensate");
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("compensate.reason", reason);
    }
}
