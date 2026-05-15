using FluentValidation;
using Haworks.Catalog.Application.Commands.Reservations;

namespace Haworks.Catalog.Application.Validators.Reservations;

public sealed class ConfirmReservationCommandValidator : AbstractValidator<ConfirmReservationCommand>
{
    public ConfirmReservationCommandValidator()
    {
        RuleFor(x => x.ReservationId).NotEqual(Guid.Empty);
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.CustomerEmail).NotEmpty().EmailAddress();
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.TotalAmount).GreaterThan(0);
    }
}
