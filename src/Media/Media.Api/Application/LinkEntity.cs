using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Media.Api.Application;

public record LinkEntityCommand(Guid MediaId, Guid EntityId, string EntityType, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<Unit>>;

public class LinkEntityValidator : AbstractValidator<LinkEntityCommand>
{
    public LinkEntityValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
        RuleFor(x => x.EntityId).NotEmpty();
        RuleFor(x => x.EntityType).NotEmpty().MaximumLength(100);
    }
}

public class LinkEntityHandler(
    MediaDbContext context,
    ICurrentUserService currentUser) : IRequestHandler<LinkEntityCommand, Result<Unit>>
{
    public async Task<Result<Unit>> Handle(LinkEntityCommand request, CancellationToken ct)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
            return Result.Failure<Unit>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));

        var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.Id == request.MediaId, ct);
        if (file == null)
            return Result.Failure<Unit>(new Error("Media.NotFound", "Media file not found."));

        if (!string.Equals(file.OwnerId, ownerId, StringComparison.Ordinal))
            return Result.Failure<Unit>(new Error("Media.Forbidden", "You do not own this media file."));

        file.SetEntityLink(request.EntityId, request.EntityType);
        await context.SaveChangesAsync(ct);

        return Unit.Value;
    }
}
