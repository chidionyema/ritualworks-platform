using Haworks.Contracts.Privacy;
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
                .PublishAsync(context => context.Init<PrivacyErasureRequested>(new PrivacyErasureRequested(
                    context.Message.RequestId, context.Message.UserId)))
                .Schedule(ErasureTimeoutSchedule, ctx => new PrivacyErasureTimedOut(ctx.Saga.CorrelationId),
                    _ => TimeSpan.FromDays(7)) // PR-02
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
                })
                .TransitionTo(Stalled)
        );

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
}
