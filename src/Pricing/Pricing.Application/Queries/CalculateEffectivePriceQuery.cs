using Haworks.Pricing.Domain.ValueObjects;
using MediatR;

namespace Haworks.Pricing.Application.Queries;

/// <summary>
/// Query to calculate the effective price for a product.
/// </summary>
public sealed record CalculateEffectivePriceQuery : IRequest<PriceBreakdownResult>
{
    public required Guid ProductId { get; init; }
    public required int Quantity { get; init; }
    public string? PromoCode { get; init; }
    public string? UserId { get; init; }
    public string? CountryCode { get; init; }
    public string? StateCode { get; init; }
}
