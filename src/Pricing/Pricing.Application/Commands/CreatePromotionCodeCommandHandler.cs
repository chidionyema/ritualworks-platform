using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

/// <summary>
/// Handles creation of a new promotion code.
/// </summary>
public sealed class CreatePromotionCodeCommandHandler : IRequestHandler<CreatePromotionCodeCommand, Guid>
{
    private readonly IPromotionCodeRepository _repository;

    public CreatePromotionCodeCommandHandler(IPromotionCodeRepository repository)
    {
        _repository = repository;
    }

    public async Task<Guid> Handle(CreatePromotionCodeCommand request, CancellationToken ct)
    {
        var code = PromotionCode.Create(
            code: request.Code,
            discountType: request.DiscountType,
            discountValue: request.DiscountValue,
            minimumOrderAmount: request.MinimumOrderAmount,
            applicableProductId: request.ApplicableProductId,
            applicableCategoryId: request.ApplicableCategoryId,
            maxUses: request.MaxUses,
            maxUsesPerUser: request.MaxUsesPerUser,
            startsAt: request.StartsAt,
            expiresAt: request.ExpiresAt,
            sellerTimezone: request.SellerTimezone);

        await _repository.AddAsync(code, ct).ConfigureAwait(false);
        await _repository.SaveChangesAsync(ct).ConfigureAwait(false);

        return code.Id;
    }
}
