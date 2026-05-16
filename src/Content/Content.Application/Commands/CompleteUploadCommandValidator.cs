using FluentValidation;

namespace Haworks.Content.Application.Commands;

internal sealed class CompleteUploadCommandValidator : AbstractValidator<CompleteUploadCommand>
{
    public CompleteUploadCommandValidator()
    {
        RuleFor(x => x.ContentId).NotEqual(Guid.Empty);
        RuleFor(x => x.OwnerUserId).NotEmpty();
    }
}
