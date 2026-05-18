using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Contracts.Media;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Media.Api.Application;

public record DeleteMediaCommand(Guid MediaId, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<Unit>>;

public class DeleteMediaValidator : AbstractValidator<DeleteMediaCommand>
{
    public DeleteMediaValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
    }
}

public class DeleteMediaHandler(
    MediaDbContext context,
    ICurrentUserService currentUser,
    IPublishEndpoint publisher,
    TimeProvider timeProvider) : IRequestHandler<DeleteMediaCommand, Result<Unit>>
{
    public async Task<Result<Unit>> Handle(DeleteMediaCommand request, CancellationToken ct)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
            return Result.Failure<Unit>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));

        var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.Id == request.MediaId, ct);
        if (file == null)
            return Result.Failure<Unit>(new Error("Media.NotFound", "Media file not found."));

        if (!string.Equals(file.OwnerId, ownerId, StringComparison.Ordinal))
            return Result.Failure<Unit>(new Error("Media.Forbidden", "You do not own this media file."));

        await using var tx = await context.Database.BeginTransactionAsync(ct);

        file.MarkAsDeleted(timeProvider);
        await context.SaveChangesAsync(ct);

        await publisher.Publish(new MediaDeletedEvent
        {
            MediaId = file.Id,
            OwnerId = file.OwnerId,
            EntityId = file.EntityId,
            EntityType = file.EntityType,
        }, ct);

        await tx.CommitAsync(ct);
        return Unit.Value;
    }
}
