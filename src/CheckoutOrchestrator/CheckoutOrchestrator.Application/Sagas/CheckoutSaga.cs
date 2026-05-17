using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Options;
using Haworks.CheckoutOrchestrator.Application.Options;
using Haworks.CheckoutOrchestrator.Application.Telemetry;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;
using Haworks.Contracts.Payments;

namespace Haworks.CheckoutOrchestrator.Application.Sagas;

/// <summary>
/// CheckoutSaga — orchestrates the cross-service checkout choreography.
///
/// Happy path:
///   Initial   --(CheckoutInitiatedEvent)-->            Initiated
///                  publishes StockReservationRequested
///   Initiated --(StockReservedEvent)-->                StockReserved
///                  publishes PaymentSessionRequested
///                  schedules PaymentExpiry timeout (15 min)
///   StockReserved --(PaymentSessionCreatedEvent)-->    ReadyForPayment
///   ReadyForPayment --(PaymentCompletedEvent)-->       Completed (final)
///                  cancels PaymentExpiry timeout
///
/// Compensation paths:
///   Initiated --(StockReservationFailedEvent)-->       Abandoned (final)
///                  no stock to release
///   StockReserved | ReadyForPayment --(PaymentSessionFailedEvent)-->  Compensating
///                  publishes StockReleaseRequested
///                  --(immediately)-->                  Abandoned (final)
///   ReadyForPayment --(PaymentExpiry timeout)-->       Compensating
///                  publishes StockReleaseRequested + CheckoutSessionExpiredEvent
///                  --(immediately)-->                  Abandoned (final)
///   ReadyForPayment --(PaymentAmountMismatchEvent)-->  RequiresReview (final-ish)
///                  no compensation; ops decides
///
/// Per ADR-0009 the saga owns no business state — only the snapshot
/// needed to drive orchestration. Order/Payment aggregates remain
/// authoritative in their respective services.
/// </summary>
public sealed class CheckoutSaga : MassTransitStateMachine<CheckoutSagaState>
{
    public CheckoutSaga(IOptions<CheckoutOptions> checkoutOptions)
    {
        var options = checkoutOptions.Value;
        InstanceState(s => s.CurrentState);

        // PaymentExpiry timeout: stock is reserved on StockReserved transition,
        // payment session lives in StockReserved + ReadyForPayment. If the
        // customer never completes payment within the deadline, the timeout
        // fires PaymentExpired which compensates the same way PaymentSessionFailed
        // does — publish StockReleaseRequested + Abandoned. Without this timer,
        // an abandoned Stripe/PayPal session leaves stock locked indefinitely.
        Schedule(
            () => PaymentExpirySchedule,
            instance => instance.PaymentExpiryTokenId,
            s =>
            {
                s.Delay = TimeSpan.FromMinutes(15);
                s.Received = r => r.CorrelateById(ctx => ctx.Message.SagaId);
            });

        Event(() => CheckoutInitiated, e => e.SelectId(ctx => ctx.Message.SagaId));
        Event(() => StockReserved, e => e.SelectId(ctx => ctx.Message.SagaId));
        Event(() => StockReservationFailed, e => e.SelectId(ctx => ctx.Message.SagaId));
        Event(() => PaymentSessionCreated, e => e.SelectId(ctx => ctx.Message.SagaId));
        Event(() => PaymentSessionFailed, e => e.SelectId(ctx => ctx.Message.SagaId));
        Event(() => PaymentCompleted, e => e.SelectId(ctx => ctx.Message.SagaId));
        Event(() => PaymentAmountMismatch, e =>
        {
            // PaymentAmountMismatchEvent doesn't carry a SagaId — correlate
            // by OrderId (one saga per order). If no saga matches, discard
            // rather than create a new saga.
            e.CorrelateBy((state, ctx) => state.OrderId == ctx.Message.OrderId);
            e.OnMissingInstance(m => m.Discard());
        });

        Initially(
            When(CheckoutInitiated)
                .Then(ctx =>
                {
                    var msg = ctx.Message;
                    var sagaState = ctx.Saga;
                    sagaState.OrderId = msg.OrderId;
                    sagaState.UserId = msg.UserId;
                    sagaState.CustomerEmail = msg.CustomerEmail;
                    sagaState.TotalAmount = msg.TotalAmount;
                    sagaState.Currency = msg.Currency ?? throw new InvalidOperationException("Currency is required on CheckoutStartedEvent");
                    sagaState.IdempotencyKey = msg.IdempotencyKey;
                    sagaState.LineItemsJson = JsonSerializer.Serialize(msg.Items);
                    sagaState.CreatedAt = DateTime.UtcNow;
                })
                .PublishAsync(ctx => ctx.Init<StockReservationRequestedEvent>(new StockReservationRequestedEvent
                {
                    OrderId = ctx.Message.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    UserId = ctx.Message.UserId,
                    CustomerEmail = ctx.Message.CustomerEmail,
                    TotalAmount = ctx.Message.TotalAmount,
                    Currency = ctx.Saga.Currency,
                    Items = ctx.Message.Items,
                    IdempotencyKey = ctx.Message.IdempotencyKey,
                }))
                .TransitionTo(Initiated));

        During(Initiated,
            When(StockReserved)
                .Then(ctx =>
                {
                    ctx.Saga.ReservedItemsJson = JsonSerializer.Serialize(ctx.Message.Items);
                })
                .PublishAsync(ctx => ctx.Init<PaymentSessionRequestedEvent>(new PaymentSessionRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Amount = ctx.Saga.TotalAmount,
                    Currency = ctx.Saga.Currency,
                    UserId = ctx.Saga.UserId,
                    CustomerEmail = ctx.Saga.CustomerEmail,
                    LineItems = ctx.Message.OrderLineItems
                        .Select(li => new PaymentLineItemData
                        {
                            Name = li.ProductName,
                            UnitAmountCents = (long)Math.Round(li.UnitPrice * 100m, 0, MidpointRounding.AwayFromZero),
                            Quantity = li.Quantity,
                        }).ToList(),
                    SuccessUrl = options.SuccessUrl,
                    CancelUrl = options.CancelUrl,
                    IdempotencyKey = ctx.Saga.IdempotencyKey,
                }))
                // Start the 15-min payment-expiry clock the moment stock is
                // reserved. The token is stored on the saga so it can be
                // cancelled when payment lands in time.
                .Schedule(
                    PaymentExpirySchedule,
                    ctx => ctx.Init<PaymentExpiredEvent>(new PaymentExpiredEvent
                    {
                        SagaId = ctx.Saga.CorrelationId,
                        OrderId = ctx.Saga.OrderId,
                    }))
                .TransitionTo(StockReservedState),
            When(StockReservationFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = $"StockReservationFailed: {ctx.Message.Reason}";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "stock_reservation_failed");
                })
                .TransitionTo(Abandoned));

        During(StockReservedState,
            When(PaymentSessionCreated)
                .Then(ctx =>
                {
                    ctx.Saga.PaymentId = ctx.Message.PaymentId;
                    ctx.Saga.PaymentSessionId = ctx.Message.SessionId;
                    ctx.Saga.PaymentCheckoutUrl = ctx.Message.CheckoutUrl;
                })
                .TransitionTo(ReadyForPayment),
            When(PaymentSessionFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = $"PaymentSessionFailed: {ctx.Message.ErrorCode}";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "payment_session_failed");
                })
                .Unschedule(PaymentExpirySchedule)
                .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
                    Reason = "payment_session_failed",
                }))
                .TransitionTo(Abandoned),
            When(PaymentExpirySchedule!.Received)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = "PaymentExpired (no payment session created within 15min)";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "payment_expired");
                })
                .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
                    Reason = "payment_expired",
                }))
                .TransitionTo(Abandoned));

        During(ReadyForPayment,
            When(PaymentCompleted)
                .Then(ctx =>
                {
                    // PaymentId already set in StockReserved transition;
                    // the PaymentCompletedEvent carries the same id.
                })
                // Customer paid in time — cancel the expiry clock so no
                // orphaned StockReleaseRequested fires later.
                .Unschedule(PaymentExpirySchedule)
                .TransitionTo(Completed)
                .Finalize(),
            When(PaymentSessionFailed)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = $"PaymentSessionFailed (mid-flight): {ctx.Message.ErrorCode}";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "payment_session_failed_post_session");
                })
                .Unschedule(PaymentExpirySchedule)
                .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
                    Reason = "payment_session_failed_post_session",
                }))
                .TransitionTo(Abandoned),
            When(PaymentExpirySchedule!.Received)
                .Then(ctx =>
                {
                    ctx.Saga.FailureReason = "PaymentExpired (customer abandoned payment session)";
                    EmitCompensateSpan(ctx.Saga.CorrelationId, ctx.Saga.OrderId, "payment_expired");
                })
                .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
                    Reason = "payment_expired",
                }))
                .TransitionTo(Abandoned),
            When(PaymentAmountMismatch)
                .Then(ctx => ctx.Saga.FailureReason =
                    $"PaymentAmountMismatch: expected={ctx.Message.ExpectedTotal}, actual={ctx.Message.ActualPaid}")
                .Unschedule(PaymentExpirySchedule)
                .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
                    Reason = "payment_amount_mismatch",
                }))
                .TransitionTo(RequiresReview));

        // Idempotency: late-arriving duplicate events on a finalized saga
        // (Completed / Abandoned / RequiresReview) silently no-op rather
        // than throwing. MT's inbox dedupes most replays; this catches
        // the rare case where the same event arrives via two paths.
        DuringAny(
            When(StockReserved).If(ctx => !string.Equals(ctx.Saga.CurrentState, Initiated.Name, StringComparison.Ordinal), ctx => ctx),
            When(PaymentCompleted).If(ctx => !string.Equals(ctx.Saga.CurrentState, ReadyForPayment.Name, StringComparison.Ordinal), ctx => ctx),
            When(PaymentSessionFailed).If(ctx => string.Equals(ctx.Saga.CurrentState, Abandoned.Name, StringComparison.Ordinal), ctx => ctx));

        SetCompletedWhenFinalized();
    }

    // States. MT convention: a property per state, plus reuse of MT's
    // Initial / Final built-ins. "StockReservedState" is renamed to avoid
    // clashing with the StockReserved Event property of the same name.
    public State Initiated { get; private set; } = null!;
    public State StockReservedState { get; private set; } = null!;
    public State ReadyForPayment { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Abandoned { get; private set; } = null!;
    public State RequiresReview { get; private set; } = null!;

    // Inbound events.
    public Event<CheckoutInitiatedEvent> CheckoutInitiated { get; private set; } = null!;
    public Event<StockReservedEvent> StockReserved { get; private set; } = null!;
    public Event<StockReservationFailedEvent> StockReservationFailed { get; private set; } = null!;
    public Event<PaymentSessionCreatedEvent> PaymentSessionCreated { get; private set; } = null!;
    public Event<PaymentSessionFailedEvent> PaymentSessionFailed { get; private set; } = null!;
    public Event<PaymentCompletedEvent> PaymentCompleted { get; private set; } = null!;
    public Event<PaymentAmountMismatchEvent> PaymentAmountMismatch { get; private set; } = null!;

    // Scheduled tick. Schedule.Received is the Event the saga reacts to
    // when the timer fires; the schedule is started via .Schedule(...) on
    // the StockReserved transition and cancelled via .Unschedule(...) on
    // any terminal transition (PaymentCompleted, PaymentSessionFailed,
    // PaymentAmountMismatch).
    public Schedule<CheckoutSagaState, PaymentExpiredEvent> PaymentExpirySchedule { get; private set; } = null!;

    /// <summary>
    /// Emits a discrete <c>checkout.saga.compensate</c> span on each
    /// compensation entry. The span is start-and-immediately-disposed —
    /// it represents the moment the saga decided to compensate, not a
    /// duration. Tags carry the failure reason and ids so Tempo can
    /// correlate the span back to the order/saga across services.
    /// </summary>
    private static void EmitCompensateSpan(Guid sagaId, Guid orderId, string reason)
    {
        using var activity = CheckoutActivities.Source.StartActivity("checkout.saga.compensate");
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("compensate.reason", reason);
    }

    private static IReadOnlyList<StockReservationItem> DeserializeItems(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<StockReservationItem>();
        try
        {
            return JsonSerializer.Deserialize<List<StockReservationItem>>(json)
                ?? new List<StockReservationItem>();
        }
        catch (JsonException)
        {
            return Array.Empty<StockReservationItem>();
        }
    }
}
