namespace Haworks.Orders.Application.DTOs;

public sealed record OrderDto(
    Guid Id,
    string UserId,
    Guid SagaId,
    string CustomerEmail,
    decimal TotalAmount,
    string Currency,
    string Status,
    Guid? PaymentId,
    string? AbandonReason,
    DateTime CreatedAt,
    IReadOnlyList<OrderItemDto> Items);

public sealed record OrderItemDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);
