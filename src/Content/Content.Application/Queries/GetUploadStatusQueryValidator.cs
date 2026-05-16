using FluentValidation;

namespace Haworks.Content.Application.Queries;

internal sealed class GetUploadStatusQueryValidator : AbstractValidator<GetUploadStatusQuery>
{
    public GetUploadStatusQueryValidator()
    {
        RuleFor(x => x.ContentId).NotEqual(Guid.Empty);
        RuleFor(x => x.OwnerUserId).NotEmpty();
    }
}
