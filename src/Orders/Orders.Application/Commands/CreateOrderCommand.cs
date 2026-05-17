using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Haworks.Contracts.Orders;
using Haworks.Orders.Application.Telemetry;

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
        using var activity = OrdersActivities.Source.StartActivity("orders.create");
        activity?.SetTag("customer.id", request.UserId);
        activity?.SetTag("order.total_cents", (long)Math.Round(request.TotalAmount * 100m, 0, MidpointRounding.AwayFromZero));
        activity?.SetTag("order.currency", request.Currency);
        activity?.SetTag("order.item_count", request.Items.Count);
        activity?.SetTag("saga.id", request.SagaId);

        // Idempotency: if an order already exists for this SagaId, return its
        // Id rather than creating a duplicate.
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

        // M2 fix: Always publish OrderCreatedEvent. If UserId is not a valid GUID,
        // derive a deterministic one from the UserId string so the saga always sees the order.
        var customerGuid = Guid.TryParse(request.UserId, out var parsed)
            ? parsed
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(request.UserId)).AsSpan(0, 16));

        if (!Guid.TryParse(request.UserId, out _))
        {
            logger.LogWarning(
                "Order {OrderId}: UserId '{UserId}' is not a Guid — using deterministic hash {DerivedGuid}",
                order.Id, request.UserId, customerGuid);
        }

        await eventPublisher.PublishAsync(new OrderCreatedEvent
        {
            OrderId = order.Id,
            CustomerId = customerGuid,
            TotalAmount = order.TotalAmount,
            CustomerEmail = order.CustomerEmail,
        }, ct);

        // M1 fix: catch unique constraint violation (23505) on SagaId for concurrent duplicates.
        // On conflict, re-read the existing order and return its ID as idempotent success.
        try
        {
            await orders.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            logger.LogInformation(
                "CreateOrderCommand: concurrent duplicate for sagaId {SagaId}; returning existing order",
                request.SagaId);
            var duplicate = await orders.GetBySagaIdTrackedAsync(request.SagaId, ct);
            if (duplicate is not null)
                return Result.Success(duplicate.Id);
            throw; // constraint was on something else
        }

        logger.LogInformation("Order {OrderId} created for sagaId {SagaId}", order.Id, request.SagaId);
        return Result.Success(order.Id);
    }
}
