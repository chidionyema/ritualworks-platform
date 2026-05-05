using Haworks.BuildingBlocks.Common;
using Haworks.Contracts.Checkout;
using MassTransit;
using MediatR;

namespace Haworks.CheckoutOrchestrator.Application.Commands;

public sealed record StartCheckoutCommand(
    Guid SagaId,
    Guid OrderId,
    string UserId,
    string CustomerEmail,
    decimal TotalAmount,
    string IdempotencyKey,
    IReadOnlyList<CheckoutItemData> Items
) : IRequest<Result<StartCheckoutResponse>>;

public sealed record StartCheckoutResponse(Guid SagaId, Guid OrderId);

internal sealed class StartCheckoutCommandHandler(
    IPublishEndpoint publishEndpoint
) : IRequestHandler<StartCheckoutCommand, Result<StartCheckoutResponse>>
{
    public async Task<Result<StartCheckoutResponse>> Handle(StartCheckoutCommand request, CancellationToken ct)
    {
        var sagaId = request.SagaId == Guid.Empty ? Guid.NewGuid() : request.SagaId;
        var orderId = request.OrderId == Guid.Empty ? Guid.NewGuid() : request.OrderId;

        await publishEndpoint.Publish(new CheckoutInitiatedEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
            UserId = request.UserId,
            CustomerEmail = request.CustomerEmail,
            TotalAmount = request.TotalAmount,
            Items = request.Items,
            IdempotencyKey = request.IdempotencyKey,
            IsGuest = false,
        }, ct);

        return Result.Success(new StartCheckoutResponse(sagaId, orderId));
    }
}
