using FluentValidation;
using Haworks.Catalog.Application.Commands;

namespace Haworks.Catalog.Application.Validators;

internal sealed class ApproveProductReviewCommandValidator : AbstractValidator<ApproveProductReviewCommand>
{
    public ApproveProductReviewCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEqual(Guid.Empty);
        RuleFor(x => x.ReviewId).NotEqual(Guid.Empty);
    }
}
