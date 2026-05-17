using Haworks.BuildingBlocks.CurrentUser;

namespace Haworks.Media.Api.Application;

public record AbortUploadCommand(Guid MediaId) : IRequest<Result<Unit>>;

public class AbortUploadValidator : AbstractValidator<AbortUploadCommand>
{
    public AbortUploadValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
    }
}

public class AbortUploadHandler(
    MediaDbContext context,
    IS3Service s3,
    ICurrentUserService currentUser,
    TimeProvider timeProvider) : IRequestHandler<AbortUploadCommand, Result<Unit>>
{
    public async Task<Result<Unit>> Handle(AbortUploadCommand request, CancellationToken ct)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
            return Result.Failure<Unit>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));

        var mediaFile = await context.MediaFiles.FirstOrDefaultAsync(f => f.Id == request.MediaId, ct);
        if (mediaFile == null)
            return Result.Failure<Unit>(new Error("Media.NotFound", "Media file not found."));

        if (!string.Equals(mediaFile.OwnerId, ownerId, StringComparison.Ordinal))
            return Result.Failure<Unit>(new Error("Media.Forbidden", "You do not own this media file."));

        if (mediaFile.Status != MediaStatus.Pending)
            return Result.Failure<Unit>(new Error("Media.InvalidState", $"Cannot abort from {mediaFile.Status} state."));

        if (mediaFile.UploadKind == UploadKind.Multipart && !string.IsNullOrEmpty(mediaFile.S3UploadId))
        {
            await s3.AbortMultipartUploadAsync(mediaFile.Id.ToString(), mediaFile.S3UploadId, ct);
        }

        mediaFile.MarkDeleted(timeProvider);
        await context.SaveChangesAsync(ct);

        return Unit.Value;
    }
}
