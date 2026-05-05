using FluentValidation;
using Haworks.Catalog.Application.Commands;

namespace Haworks.Catalog.Application.Validators;

internal sealed class UpdateCategoryCommandValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryCommandValidator()
    {
        RuleFor(x => x.CategoryId).NotEqual(Guid.Empty);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}
