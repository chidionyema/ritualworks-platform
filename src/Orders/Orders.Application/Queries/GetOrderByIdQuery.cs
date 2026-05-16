using MediatR;
using Haworks.Orders.Application.DTOs;

namespace Haworks.Orders.Application.Queries;

public sealed record GetOrderByIdQuery(Guid Id, string UserId) : IRequest<Result<OrderDto>>;

internal sealed class GetOrderByIdQueryHandler(IOrderRepository orders)
    : IRequestHandler<GetOrderByIdQuery, Result<OrderDto>>
{
    public async Task<Result<OrderDto>> Handle(GetOrderByIdQuery request, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(request.Id, ct);
        if (order is null)
        {
            return Result.Failure<OrderDto>(Error.Orders.NotFoundWithId(request.Id));
        }

        if (order.UserId != request.UserId)
        {
            return Result.Failure<OrderDto>(Error.Orders.Forbidden);
        }

        return Result.Success(MapToDto(order));
    }
...
    internal static OrderDto MapToDto(Order order) => new(
        order.Id,
        order.UserId,
        order.SagaId,
        order.CustomerEmail,
        order.TotalAmount,
        order.Currency,
        order.Status.ToString(),
        order.PaymentId,
        order.AbandonReason,
        order.CreatedAt,
        order.Items.Select(i => new OrderItemDto(
            i.Id, i.ProductId, i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList());
}
