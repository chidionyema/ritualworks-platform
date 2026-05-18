using Haworks.BuildingBlocks.Common;
using Haworks.Orders.Application.DTOs;
using Haworks.Orders.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Orders.Application.Queries;

public sealed record GetGuestOrderQuery(
    string Token,
    string Email
) : IRequest<Result<OrderDto>>;

internal sealed class GetGuestOrderQueryHandler(
    IOrderRepository orderRepository,
    ILogger<GetGuestOrderQueryHandler> logger)
    : IRequestHandler<GetGuestOrderQuery, Result<OrderDto>>
{
    public async Task<Result<OrderDto>> Handle(
        GetGuestOrderQuery request,
        CancellationToken cancellationToken)
    {
        var guestInfo = await orderRepository.GetGuestByTokenAsync(request.Token, cancellationToken);

        if (guestInfo == null)
        {
            logger.LogWarning("Guest order not found for token");
            return Result.Failure<OrderDto>(new Error("Orders.NotFound", "Order not found"));
        }

        if (!string.Equals(guestInfo.Email, request.Email, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Email mismatch for guest order lookup");
            return Result.Failure<OrderDto>(new Error("Orders.NotFound", "Order not found"));
        }

        var order = await orderRepository.GetByIdAsync(guestInfo.OrderId, cancellationToken);

        if (order == null)
        {
            return Result.Failure<OrderDto>(new Error("Orders.NotFound", "Order not found"));
        }

        var dto = new OrderDto(
            order.Id,
            order.UserId,
            order.SagaId,
            order.CustomerEmail,
            order.TotalAmountCents / 100m,
            order.Currency,
            order.Status.ToString(),
            order.PaymentId,
            order.AbandonReason,
            order.CreatedAt,
            order.Items.Select(i => new OrderItemDto(
                i.Id,
                i.ProductId,
                i.ProductName,
                i.Quantity,
                i.UnitPriceCents / 100m,
                (i.Quantity * i.UnitPriceCents) / 100m)).ToList(),
            guestInfo.OrderToken);

        return Result.Success(dto);
    }
}
