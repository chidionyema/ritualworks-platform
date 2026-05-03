using System.Text.Json;
using MassTransit;
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
    public CheckoutSaga()
    {
        InstanceState(s => s.CurrentState);

        // TODO Phase 5+: payment-expiry timeout schedule.
        // The MT pattern is `Schedule(() => PaymentExpiry, …)` + `Then(ctx => ctx.Schedule(...))`
        // on the StockReserved transition, and `Then(ctx => ctx.Unschedule(...))` on the
        // PaymentCompleted transition. Requires a delayed-message scheduler — either
        // RabbitMQ's delayed-message-exchange plugin (one-time broker setup), Quartz.NET
        // (separate persistence), or `services.AddDelayedMessageScheduler()` for in-memory
        // dev transport. Compensation triggered by the timeout uses the same code path as
        // PaymentSessionFailed (publish StockReleaseRequested + transition Abandoned).
        // Out of Phase 5 scope; the StockReservationFailed and PaymentSessionFailed
        // compensation paths below cover the failure modes that don't require a wall-clock
        // timer.

        Event(() => CheckoutInitiated, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => StockReserved, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => StockReservationFailed, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => PaymentSessionCreated, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => PaymentSessionFailed, e => e.CorrelateById(ctx => ctx.Message.SagaId));
        Event(() => PaymentCompleted, e => e.CorrelateById(ctx => ctx.Message.SagaId));
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
                    sagaState.Currency = "USD";
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
                    Currency = "USD",
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
                    CustomerEmail = ctx.Saga.CustomerEmail,
                    LineItems = ctx.Message.OrderLineItems
                        .Select(li => new PaymentLineItemData
                        {
                            Name = li.ProductName,
                            UnitAmountCents = (long)(li.UnitPrice * 100m),
                            Quantity = li.Quantity,
                        }).ToList(),
                    SuccessUrl = "https://app.example.com/checkout/success",
                    CancelUrl = "https://app.example.com/checkout/cancel",
                    IdempotencyKey = ctx.Saga.IdempotencyKey,
                }))
                .TransitionTo(StockReservedState),
            When(StockReservationFailed)
                .Then(ctx => ctx.Saga.FailureReason = $"StockReservationFailed: {ctx.Message.Reason}")
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
                .Then(ctx => ctx.Saga.FailureReason = $"PaymentSessionFailed: {ctx.Message.ErrorCode}")
                .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
                    Reason = "payment_session_failed",
                }))
                .TransitionTo(Abandoned));

        During(ReadyForPayment,
            When(PaymentCompleted)
                .Then(ctx =>
                {
                    // PaymentId already set in StockReserved transition;
                    // the PaymentCompletedEvent carries the same id.
                })
                .TransitionTo(Completed)
                .Finalize(),
            When(PaymentSessionFailed)
                .Then(ctx => ctx.Saga.FailureReason = $"PaymentSessionFailed (mid-flight): {ctx.Message.ErrorCode}")
                .PublishAsync(ctx => ctx.Init<StockReleaseRequestedEvent>(new StockReleaseRequestedEvent
                {
                    OrderId = ctx.Saga.OrderId,
                    SagaId = ctx.Saga.CorrelationId,
                    Items = DeserializeItems(ctx.Saga.ReservedItemsJson),
                    Reason = "payment_session_failed_post_session",
                }))
                .TransitionTo(Abandoned),
            When(PaymentAmountMismatch)
                .Then(ctx => ctx.Saga.FailureReason =
                    $"PaymentAmountMismatch: expected={ctx.Message.ExpectedTotal}, actual={ctx.Message.ActualPaid}")
                .TransitionTo(RequiresReview));

        // Idempotency: late-arriving duplicate events on a finalized saga
        // (Completed / Abandoned / RequiresReview) silently no-op rather
        // than throwing. MT's inbox dedupes most replays; this catches
        // the rare case where the same event arrives via two paths.
        DuringAny(
            When(StockReserved).If(ctx => ctx.Saga.CurrentState != Initiated.Name, ctx => ctx),
            When(PaymentCompleted).If(ctx => ctx.Saga.CurrentState != ReadyForPayment.Name, ctx => ctx),
            When(PaymentSessionFailed).If(ctx => ctx.Saga.CurrentState == Abandoned.Name, ctx => ctx));

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
