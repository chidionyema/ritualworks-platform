using Haworks.BuildingBlocks.Common;
using Haworks.CheckoutOrchestrator.Application.Telemetry;
using Haworks.CheckoutOrchestrator.Application.Interfaces;
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
    IReadOnlyList<CheckoutItemData> Items,
    string? Currency = null
) : IRequest<Result<StartCheckoutResponse>>;

public sealed record StartCheckoutResponse(Guid SagaId, Guid OrderId);

internal sealed class StartCheckoutCommandHandler(
    IPublishEndpoint publishEndpoint,
    ICheckoutDbContext db
) : IRequestHandler<StartCheckoutCommand, Result<StartCheckoutResponse>>
{
    public async Task<Result<StartCheckoutResponse>> Handle(StartCheckoutCommand request, CancellationToken ct)
    {
        var sagaId = request.SagaId == Guid.Empty ? Guid.NewGuid() : request.SagaId;
        var orderId = request.OrderId == Guid.Empty ? Guid.NewGuid() : request.OrderId;

        using var activity = CheckoutActivities.Source.StartActivity("checkout.saga.start");
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("order.id", orderId);
        activity?.SetTag("customer.id", request.UserId);
        activity?.SetTag("checkout.total_amount_cents", (long)Math.Round(request.TotalAmount * 100m, 0, MidpointRounding.AwayFromZero));
        activity?.SetTag("checkout.item_count", request.Items.Count);

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
            Currency = request.Currency,
        }, ct);

        await db.SaveChangesAsync(ct);

        return Result.Success(new StartCheckoutResponse(sagaId, orderId));
    }
}
