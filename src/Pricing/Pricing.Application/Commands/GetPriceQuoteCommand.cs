using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

public sealed record GetPriceQuoteCommand(
    IEnumerable<CartLineDto> Lines,
    Guid? CustomerId = null,
    string? Locale = null,
    string? CouponCode = null) : IRequest<Result<PriceQuoteDto>>;

public sealed record CartLineDto(
    Guid ProductId,
    int Quantity,
    decimal UnitPrice);

public sealed record PriceQuoteDto(
    IEnumerable<CartLineDiscountDto> Lines,
    decimal TotalDiscount,
    decimal FinalPrice);

public sealed record CartLineDiscountDto(
    Guid ProductId,
    decimal DiscountAmount,
    decimal FinalPrice);
