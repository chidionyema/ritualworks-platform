using MediatR;
using Haworks.Orders.Application.DTOs;

namespace Haworks.Orders.Application.Queries;

public sealed record ListUserOrdersQuery(string UserId, int Skip, int Take)
    : IRequest<Result<PagedResult<OrderDto>>>;

internal sealed class ListUserOrdersQueryHandler(IOrderRepository orders)
    : IRequestHandler<ListUserOrdersQuery, Result<PagedResult<OrderDto>>>
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedResult<OrderDto>>> Handle(ListUserOrdersQuery request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Result.Failure<PagedResult<OrderDto>>(Error.Users.MissingUserId);
        }

        var skip = Math.Max(0, request.Skip);
        var take = Math.Clamp(request.Take <= 0 ? 20 : request.Take, 1, MaxPageSize);

        var items = await orders.ListByUserAsync(request.UserId, skip, take, ct);
        var total = await orders.CountByUserAsync(request.UserId, ct);

        var dtos = items.Select(GetOrderByIdQueryHandler.MapToDto).ToList();
        return Result.Success(new PagedResult<OrderDto>(dtos, total, skip, take));
    }
}
