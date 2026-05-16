using FluentValidation;

namespace Haworks.Merchant.Application.Merchants.Commands.DeactivateMerchant;

internal sealed class DeactivateMerchantCommandValidator : AbstractValidator<DeactivateMerchantCommand>
{
    public DeactivateMerchantCommandValidator()
    {
        RuleFor(x => x.MerchantId).NotEqual(Guid.Empty);
        RuleFor(x => x.UserId).NotEqual(Guid.Empty);
    }
}
