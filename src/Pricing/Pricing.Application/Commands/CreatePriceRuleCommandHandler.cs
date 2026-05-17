using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

/// <summary>
/// Handles creation of a new price rule.
/// </summary>
public sealed class CreatePriceRuleCommandHandler : IRequestHandler<CreatePriceRuleCommand, Guid>
{
    private readonly IPriceRuleRepository _repository;

    public CreatePriceRuleCommandHandler(IPriceRuleRepository repository)
    {
        _repository = repository;
    }

    public async Task<Guid> Handle(CreatePriceRuleCommand request, CancellationToken ct)
    {
        var rule = PriceRule.Create(
            productId: request.ProductId,
            categoryId: request.CategoryId,
            priority: request.Priority,
            discountType: request.DiscountType,
            discountValue: request.DiscountValue,
            minimumQuantity: request.MinimumQuantity,
            maximumQuantity: request.MaximumQuantity,
            startsAt: request.StartsAt,
            expiresAt: request.ExpiresAt,
            sellerTimezone: request.SellerTimezone);

        await _repository.AddAsync(rule, ct).ConfigureAwait(false);
        await _repository.SaveChangesAsync(ct).ConfigureAwait(false);

        return rule.Id;
    }
}
