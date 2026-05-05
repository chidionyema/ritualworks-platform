using FluentValidation;
using MediatR;
using Haworks.Contracts.Orders;

namespace Haworks.Orders.Application.Commands;

public sealed record CreateOrderCommand(
    string UserId,
    string CustomerEmail,
    decimal TotalAmount,
    string Currency,
    Guid SagaId,
    string IdempotencyKey,
    IReadOnlyList<CreateOrderLineItem> Items) : IRequest<Result<Guid>>;

public sealed record CreateOrderLineItem(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);


internal sealed class CreateOrderCommandHandler(
    IOrderRepository orders,
    IDomainEventPublisher eventPublisher,
    ILogger<CreateOrderCommandHandler> logger
) : IRequestHandler<CreateOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateOrderCommand request, CancellationToken ct)
    {
        // Idempotency: if an order already exists for this SagaId, return its
        // Id rather than creating a duplicate. SagaId has a unique index on
        // the Orders table — the alternative is hitting that constraint at
        // SaveChanges time and translating to Conflict, which is uglier UX
        // for a deliberately-retryable command.
        var existing = await orders.GetBySagaIdTrackedAsync(request.SagaId, ct);
        if (existing is not null)
        {
            logger.LogInformation(
                "CreateOrderCommand: returning existing order {OrderId} for sagaId {SagaId}",
                existing.Id, request.SagaId);
            return Result.Success(existing.Id);
        }

        var order = Order.Create(
            request.UserId,
            request.TotalAmount,
            request.Currency,
            request.SagaId,
            request.IdempotencyKey,
            request.CustomerEmail,
            request.Items.Select(i => (i.ProductId, i.ProductName, i.Quantity, i.UnitPrice)));

        await orders.AddAsync(order, ct);

        // Publish BEFORE save so the OutboxMessage commits in the same EF txn
        // as the Order INSERT. CustomerId on the contract is Guid (required),
        // so if the UserId can't be parsed as a Guid we skip the publish —
        // the order still saves so REST queries find it. Phase 5+ saga lookups
        // will revisit when checkout-svc supplies the canonical Guid CustomerId.
        if (Guid.TryParse(request.UserId, out var customerGuid))
        {
            await eventPublisher.PublishAsync(new OrderCreatedEvent
            {
                OrderId = order.Id,
                CustomerId = customerGuid,
                TotalAmount = order.TotalAmount,
                CustomerEmail = order.CustomerEmail,
            }, ct);
        }
        else
        {
            logger.LogWarning(
                "Order {OrderId}: UserId '{UserId}' is not a Guid — skipping OrderCreatedEvent. " +
                "Downstream consumers won't see this order until UserId-as-Guid is enforced upstream.",
                order.Id, request.UserId);
        }

        await orders.SaveChangesAsync(ct);
        logger.LogInformation("Order {OrderId} created for sagaId {SagaId}", order.Id, request.SagaId);
        return Result.Success(order.Id);
    }
}
