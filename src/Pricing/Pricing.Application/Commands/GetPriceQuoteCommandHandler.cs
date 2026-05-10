using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

internal sealed class GetPriceQuoteCommandHandler : IRequestHandler<GetPriceQuoteCommand, Result<PriceQuoteDto>>
{
    // TODO(pricing-T2): Inject IPromotionResolver and IDiscountCalculator
    
    public Task<Result<PriceQuoteDto>> Handle(GetPriceQuoteCommand request, CancellationToken ct)
    {
        // For now, return a stub quote with no discounts. 
        // Actual logic will be implemented by T2 in Pricing.Application/Promotions.
        var quoteLines = request.Lines.Select(l => new CartLineDiscountDto(
            l.ProductId,
            0,
            l.UnitPrice * l.Quantity)).ToList();

        var quote = new PriceQuoteDto(
            quoteLines,
            0,
            quoteLines.Sum(l => l.FinalPrice));

        return Task.FromResult(Result.Success(quote));
    }
}
