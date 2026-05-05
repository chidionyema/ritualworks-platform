using FluentValidation;
using Haworks.Catalog.Application.Commands;

namespace Haworks.Catalog.Application.Validators;

public sealed class ReserveStockCommandValidator : AbstractValidator<ReserveStockCommand>
{
    public ReserveStockCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEqual(Guid.Empty);
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.OrderId).NotEqual(Guid.Empty);
        RuleFor(x => x.SagaId).NotEqual(Guid.Empty);
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.TotalAmount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.CustomerEmail).NotEmpty().EmailAddress();
    }
}
