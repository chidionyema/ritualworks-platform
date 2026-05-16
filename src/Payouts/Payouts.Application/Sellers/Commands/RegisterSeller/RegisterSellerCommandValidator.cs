using FluentValidation;

namespace Haworks.Payouts.Application.Sellers.Commands.RegisterSeller;

public sealed class RegisterSellerCommandValidator : AbstractValidator<RegisterSellerCommand>
{
    public RegisterSellerCommandValidator()
    {
        RuleFor(x => x.SellerId).NotEmpty();
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}
