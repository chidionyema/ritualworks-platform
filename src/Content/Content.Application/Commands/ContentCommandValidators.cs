using FluentValidation;

namespace Haworks.Content.Application.Commands;

public class DeleteContentCommandValidator : AbstractValidator<DeleteContentCommand>
{
    public DeleteContentCommandValidator()
    {
        RuleFor(x => x.ContentId).NotEmpty();
        RuleFor(x => x.OwnerUserId).NotEmpty();
    }
}

public class AbortUploadCommandValidator : AbstractValidator<AbortUploadCommand>
{
    public AbortUploadCommandValidator()
    {
        RuleFor(x => x.ContentId).NotEmpty();
        RuleFor(x => x.OwnerUserId).NotEmpty();
    }
}
