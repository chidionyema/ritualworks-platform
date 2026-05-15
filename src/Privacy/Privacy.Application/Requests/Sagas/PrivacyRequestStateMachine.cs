using Haworks.Contracts.Privacy;
using Haworks.Privacy.Application.Telemetry;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Privacy.Application.Requests.Sagas;

public class PrivacyRequestStateMachine : MassTransitStateMachine<PrivacyRequestState>
{
    private readonly ILogger<PrivacyRequestStateMachine> _logger;

    public PrivacyRequestStateMachine(ILogger<PrivacyRequestStateMachine> logger)
    {
        _logger = logger;
        InstanceState(x => x.CurrentState);

        // PR-02: 7-day timeout for GDPR compliance
        Schedule(() => ErasureTimeoutSchedule, instance => instance.ErasureTimeoutTokenId, s =>
        {
            s.Received = r => r.CorrelateById(ctx => ctx.Message.RequestId);
        });

        Event(() => RequestInitiated, x => x.CorrelateById(m => m.Message.RequestId));
        Event(() => ErasureCompleted, x => x.CorrelateById(m => m.Message.RequestId));
        Event(() => ErasureFailed, x => x.CorrelateById(m => m.Message.RequestId)); // PR-03

        Initially(
            When(RequestInitiated)
                .Then(context =>
                {
                    context.Saga.UserId = context.Message.UserId;
                    context.Saga.RequestType = "Erasure"; // PR-06
                    context.Saga.CreatedAt = DateTime.UtcNow;

                    _logger.LogInformation("Privacy erasure request initiated for user {UserId}, request {RequestId}",
                        context.Message.UserId, context.Message.RequestId);
                })
                .PublishAsync(context => context.Init<PrivacyErasureRequested>(new PrivacyErasureRequested
                {
                    RequestId = context.Message.RequestId,
                    UserId = context.Message.UserId
                }))
                .Schedule(ErasureTimeoutSchedule, ctx => new PrivacyErasureTimedOut { RequestId = ctx.Saga.CorrelationId },
                    _ => TimeSpan.FromDays(7))
                .TransitionTo(Processing)
        );

        During(Processing,
            When(ErasureCompleted)
                .Then(context =>
                {
                    switch (context.Message.ServiceName)
                    {
                        case "identity-svc":
                            context.Saga.IdentityCompleted = true;
                            break;
                        case "orders-svc":
                            context.Saga.OrdersCompleted = true;
                            break;
                        case "payments-svc": // PR-01: track payments erasure
                            context.Saga.PaymentsCompleted = true;
                            break;
                        default:
                            _logger.LogWarning("Privacy saga received ErasureCompleted for unknown service: {ServiceName}",
                                context.Message.ServiceName); // PR-07
                            break;
                    }
                })
                // PR-01: require all 3 services before completing
                .If(context => context.Saga.IdentityCompleted
                            && context.Saga.OrdersCompleted
                            && context.Saga.PaymentsCompleted,
                    binder => binder
                        .Then(ctx =>
                        {
                            ctx.Saga.CompletedAt = DateTime.UtcNow; // PR-05
                            _logger.LogInformation("Privacy erasure completed for user {UserId}, request {RequestId}",
                                ctx.Saga.UserId, ctx.Saga.CorrelationId);
                            EmitSpan(ctx.Saga.CorrelationId, ctx.Saga.UserId, "erasure_completed");
                        })
                        .Unschedule(ErasureTimeoutSchedule)
                        .TransitionTo(Completed)
                        .Finalize()), // PR-04

            // PR-03: handle erasure failures
            When(ErasureFailed)
                .Then(context =>
                {
                    _logger.LogError("Privacy erasure failed for service {ServiceName}, request {RequestId}: {Error}",
                        context.Message.ServiceName, context.Message.RequestId, context.Message.ErrorMessage);
                    EmitSpan(context.Saga.CorrelationId, context.Saga.UserId, "erasure_failed");
                })
                .Unschedule(ErasureTimeoutSchedule)
                .TransitionTo(Failed),

            // PR-02: handle timeout
            When(ErasureTimeoutSchedule.Received)
                .Then(context =>
                {
                    _logger.LogError("Privacy erasure timed out for request {RequestId}. Identity={Identity}, Orders={Orders}, Payments={Payments}",
                        context.Saga.CorrelationId, context.Saga.IdentityCompleted,
                        context.Saga.OrdersCompleted, context.Saga.PaymentsCompleted);
                    EmitSpan(context.Saga.CorrelationId, context.Saga.UserId, "erasure_stalled");
                })
                .TransitionTo(Stalled)
        );

        // Idempotency: late-arriving or duplicate events on a finalized saga
        // (Completed / Failed / Stalled) silently no-op rather than throwing.
        // MT's inbox dedupes most replays; this catches the rare case where
        // the same event arrives via two paths or after the saga has moved on.
        DuringAny(
            When(ErasureCompleted)
                .If(ctx => ctx.Saga.CurrentState == nameof(Completed)
                        || ctx.Saga.CurrentState == nameof(Failed)
                        || ctx.Saga.CurrentState == nameof(Stalled),
                    binder => binder.Then(ctx =>
                        _logger.LogInformation(
                            "Ignoring late ErasureCompleted for request {RequestId} in state {State}",
                            ctx.Saga.CorrelationId, ctx.Saga.CurrentState))),
            When(ErasureFailed)
                .If(ctx => ctx.Saga.CurrentState == nameof(Completed)
                        || ctx.Saga.CurrentState == nameof(Failed),
                    binder => binder.Then(ctx =>
                        _logger.LogInformation(
                            "Ignoring late ErasureFailed for request {RequestId} in state {State}",
                            ctx.Saga.CorrelationId, ctx.Saga.CurrentState))));

        SetCompletedWhenFinalized(); // PR-04
    }

    public State Processing { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Failed { get; private set; } = null!;    // PR-03
    public State Stalled { get; private set; } = null!;   // PR-02

    public Event<InitiatePrivacyRequestMessage> RequestInitiated { get; private set; } = null!;
    public Event<PrivacyErasureCompleted> ErasureCompleted { get; private set; } = null!;
    public Event<PrivacyErasureFailed> ErasureFailed { get; private set; } = null!; // PR-03

    public Schedule<PrivacyRequestState, PrivacyErasureTimedOut> ErasureTimeoutSchedule { get; private set; } = null!; // PR-02

    /// <summary>
    /// Emits a discrete <c>privacy.saga.transition</c> span on key state
    /// transitions. The span is start-and-immediately-disposed — it marks
    /// the moment, not a duration. Tags carry ids and the reason so Tempo
    /// can correlate the span across services.
    /// </summary>
    private static void EmitSpan(Guid requestId, Guid userId, string reason)
    {
        using var activity = PrivacyActivities.Source.StartActivity("privacy.saga.transition");
        activity?.SetTag("saga.id", requestId);
        activity?.SetTag("user.id", userId);
        activity?.SetTag("transition.reason", reason);
    }
}
