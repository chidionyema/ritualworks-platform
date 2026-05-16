using System.Text.RegularExpressions;
using Haworks.BuildingBlocks.CurrentUser;

namespace Haworks.Media.Api.Application;

public record GetMediaUrlQuery(Guid MediaId, string? Variant = null) : IRequest<Result<MediaUrlResponse>>;
public record MediaUrlResponse(string Url, DateTime ExpiresAt);

public partial class GetMediaUrlValidator : AbstractValidator<GetMediaUrlQuery>
{
    [GeneratedRegex(@"^[a-zA-Z0-9_\-/\.]{1,100}$")]
    private static partial Regex SafeVariantPattern();

    public GetMediaUrlValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
        RuleFor(x => x.Variant)
            .Must(v => v == null || SafeVariantPattern().IsMatch(v))
            .WithMessage("Variant contains invalid characters.")
            .Must(v => v == null || !v.Contains("..", StringComparison.Ordinal))
            .WithMessage("Path traversal is not allowed.");
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

        if (file.Status != MediaStatus.Active)
            return Result.Failure<MediaUrlResponse>(new Error("Media.NotReady",
                $"File is in {file.Status} state and cannot be served."));

        var s3Key = string.IsNullOrEmpty(request.Variant)
            ? file.Id.ToString()
            : $"media/{file.Id}/{request.Variant}";

        var url = s3.GeneratePresignedGetUrl(s3Key);
        var expiresAt = DateTime.UtcNow.AddMinutes(60);

        return new MediaUrlResponse(url, expiresAt);
    }
}
