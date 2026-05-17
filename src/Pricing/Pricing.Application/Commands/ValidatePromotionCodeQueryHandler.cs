using Haworks.Pricing.Application.Interfaces;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

/// <summary>
/// Handles promotion code validation.
/// </summary>
public sealed class ValidatePromotionCodeQueryHandler : IRequestHandler<ValidatePromotionCodeQuery, ValidatePromotionCodeResult>
{
    private readonly IPromotionCodeRepository _repository;

    public ValidatePromotionCodeQueryHandler(IPromotionCodeRepository repository)
    {
        _repository = repository;
    }

    public async Task<ValidatePromotionCodeResult> Handle(ValidatePromotionCodeQuery request, CancellationToken ct)
    {
        var code = await _repository.GetByCodeAsync(request.Code.ToUpperInvariant(), ct).ConfigureAwait(false);
        if (code is null)
        {
            return new ValidatePromotionCodeResult { Valid = false, Reason = "NotFound" };
        }

        var now = DateTimeOffset.UtcNow;
        if (!code.CanRedeem(now))
        {
            string reason;
            if (code.MaxUses.HasValue && code.UsesCount >= code.MaxUses.Value)
                reason = "Exhausted";
            else if (code.ExpiresAt.HasValue && now >= code.ExpiresAt.Value)
                reason = "Expired";
            else
                reason = "Inactive";

            return new ValidatePromotionCodeResult { Valid = false, Reason = reason };
        }

        if (!code.IsApplicableTo(request.ProductId, null))
        {
            return new ValidatePromotionCodeResult { Valid = false, Reason = "NotApplicable" };
        }

        return new ValidatePromotionCodeResult
        {
            Valid = true,
            DiscountType = code.DiscountType.ToString(),
            Value = code.DiscountValue,
            ExpiresAt = code.ExpiresAt,
        };
    }
}
