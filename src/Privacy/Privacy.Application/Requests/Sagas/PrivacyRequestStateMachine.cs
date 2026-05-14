using Haworks.Contracts.Privacy;
using MassTransit;

namespace Haworks.Privacy.Application.Requests.Sagas;

public class PrivacyRequestStateMachine : MassTransitStateMachine<PrivacyRequestState>
{
    public PrivacyRequestStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => RequestInitiated, x => x.CorrelateById(m => m.Message.RequestId));
        Event(() => ErasureCompleted, x => x.CorrelateById(m => m.Message.RequestId));

        Initially(
            When(RequestInitiated)
                .Then(context =>
                {
                    context.Saga.UserId = context.Message.UserId;
                    context.Saga.CreatedAt = DateTime.UtcNow;
                })
                .PublishAsync(context => context.Init<PrivacyErasureRequested>(new { RequestId = context.Message.RequestId, UserId = context.Message.UserId }))
                .TransitionTo(Processing)
        );

        During(Processing,
            When(ErasureCompleted)
                .Then(context =>
                {
                    if (context.Message.ServiceName == "identity-svc") context.Saga.IdentityCompleted = true;
                    if (context.Message.ServiceName == "orders-svc") context.Saga.OrdersCompleted = true;
                })
                .If(context => context.Saga.IdentityCompleted && context.Saga.OrdersCompleted,
                    binder => binder.TransitionTo(Completed))
        );
    }

    public State Processing { get; private set; } = null!;
    public State Completed { get; private set; } = null!;

    public Event<InitiatePrivacyRequestMessage> RequestInitiated { get; private set; } = null!;
    public Event<PrivacyErasureCompleted> ErasureCompleted { get; private set; } = null!;
}

public record InitiatePrivacyRequestMessage(Guid RequestId, Guid UserId);
