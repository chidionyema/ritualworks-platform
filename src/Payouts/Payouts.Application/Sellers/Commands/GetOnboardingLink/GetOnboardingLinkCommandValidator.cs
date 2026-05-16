using FluentValidation;

namespace Haworks.Payouts.Application.Sellers.Commands.GetOnboardingLink;

public sealed class GetOnboardingLinkCommandValidator : AbstractValidator<GetOnboardingLinkCommand>
{
    public GetOnboardingLinkCommandValidator()
    {
        RuleFor(x => x.SellerId).NotEmpty();
        RuleFor(x => x.ReturnUrl).NotEmpty();
        RuleFor(x => x.RefreshUrl).NotEmpty();
    }
}
