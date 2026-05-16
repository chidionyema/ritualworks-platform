using FluentValidation;

namespace Haworks.Merchant.Application.Merchants.Commands.ApproveMerchant;

internal sealed class ApproveMerchantCommandValidator : AbstractValidator<ApproveMerchantCommand>
{
    public ApproveMerchantCommandValidator()
    {
        RuleFor(x => x.MerchantId).NotEqual(Guid.Empty);
        RuleFor(x => x.ApprovedBy).NotEmpty().MaximumLength(200);
    }
}
