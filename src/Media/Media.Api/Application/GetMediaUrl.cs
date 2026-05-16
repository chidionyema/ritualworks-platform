using Haworks.BuildingBlocks.CurrentUser;

namespace Haworks.Media.Api.Application;

public record GetMediaUrlQuery(Guid MediaId, string? Variant = null) : IRequest<Result<MediaUrlResponse>>;
public record MediaUrlResponse(string Url, DateTime ExpiresAt);

public class GetMediaUrlValidator : AbstractValidator<GetMediaUrlQuery>
{
    public GetMediaUrlValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
    }
}

public class GetMediaUrlHandler(
    MediaDbContext context,
    IS3Service s3,
    ICurrentUserService currentUser) : IRequestHandler<GetMediaUrlQuery, Result<MediaUrlResponse>>
{
    public async Task<Result<MediaUrlResponse>> Handle(GetMediaUrlQuery request, CancellationToken ct)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
            return Result.Failure<MediaUrlResponse>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));

        var file = await context.MediaFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == request.MediaId, ct);

        if (file == null)
            return Result.Failure<MediaUrlResponse>(new Error("Media.NotFound", "Media file not found."));

        if (!string.Equals(file.OwnerId, ownerId, StringComparison.Ordinal))
            return Result.Failure<MediaUrlResponse>(new Error("Media.Forbidden", "You do not own this media file."));

        if (file.Status == MediaStatus.Rejected)
            return Result.Failure<MediaUrlResponse>(new Error("Media.ScanFailed", "This file failed virus scanning and cannot be served."));

        // Determine S3 key: original file or a variant
        var s3Key = string.IsNullOrEmpty(request.Variant)
            ? file.Id.ToString()
            : $"media/{file.Id}/{request.Variant}";

        var url = s3.GeneratePresignedGetUrl(s3Key);
        var expiresAt = DateTime.UtcNow.AddMinutes(60);

        return new MediaUrlResponse(url, expiresAt);
    }
}
