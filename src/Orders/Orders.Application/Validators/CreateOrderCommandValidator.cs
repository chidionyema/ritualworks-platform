using FluentValidation;
using Haworks.Orders.Application.Commands;

namespace Haworks.Orders.Application.Validators;

public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.CustomerEmail).NotEmpty().EmailAddress();
        RuleFor(x => x.TotalAmount).GreaterThan(0);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.SagaId).NotEqual(Guid.Empty);
        RuleFor(x => x.IdempotencyKey).NotEmpty();
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEqual(Guid.Empty);
            item.RuleFor(i => i.ProductName).NotEmpty();
            item.RuleFor(i => i.Quantity).GreaterThan(0);
            item.RuleFor(i => i.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}
