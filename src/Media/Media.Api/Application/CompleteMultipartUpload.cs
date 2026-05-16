using Amazon.S3.Model;
using Haworks.BuildingBlocks.CurrentUser;

namespace Haworks.Media.Api.Application;

public record CompleteMultipartUploadCommand(Guid MediaId, IReadOnlyList<PartETagDto> Parts) : IRequest<Result<Unit>>;
public record PartETagDto(int PartNumber, string ETag);

public class CompleteMultipartUploadValidator : AbstractValidator<CompleteMultipartUploadCommand>
{
    public CompleteMultipartUploadValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
        RuleFor(x => x.Parts).NotEmpty().WithMessage("Parts list is required for multipart completion.");
        RuleForEach(x => x.Parts).ChildRules(p =>
        {
            p.RuleFor(x => x.PartNumber).GreaterThan(0);
            p.RuleFor(x => x.ETag).NotEmpty();
        });
    }
}

public class CompleteMultipartUploadHandler(
    MediaDbContext context,
    IS3Service s3,
    IVirusScanner virusScanner,
    ICurrentUserService currentUser) : IRequestHandler<CompleteMultipartUploadCommand, Result<Unit>>
{
    public async Task<Result<Unit>> Handle(CompleteMultipartUploadCommand request, CancellationToken ct)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
            return Result.Failure<Unit>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));

        var mediaFile = await context.MediaFiles.FirstOrDefaultAsync(f => f.Id == request.MediaId, ct);
        if (mediaFile == null)
            return Result.Failure<Unit>(new Error("Media.NotFound", "Media file not found."));

        if (!string.Equals(mediaFile.OwnerId, ownerId, StringComparison.Ordinal))
            return Result.Failure<Unit>(new Error("Media.Forbidden", "You do not own this media file."));

        if (mediaFile.UploadKind != UploadKind.Multipart || string.IsNullOrEmpty(mediaFile.S3UploadId))
            return Result.Failure<Unit>(new Error("Media.NotMultipart", "This file was not initiated as a multipart upload."));

        if (mediaFile.Status != MediaStatus.Pending)
            return Result.Failure<Unit>(new Error("Media.InvalidState", $"Cannot complete upload from {mediaFile.Status} state."));

        var parts = request.Parts
            .OrderBy(p => p.PartNumber)
            .Select(p => new PartETag(p.PartNumber, p.ETag))
            .ToList();

        // Stitch parts in S3
        await s3.CompleteMultipartUploadAsync(mediaFile.Id.ToString(), mediaFile.S3UploadId, parts, ct);

        // Trigger virus scan — same as single-part POST /complete
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        try
        {
            mediaFile.MarkAsQuarantined();
            await context.SaveChangesAsync(ct);

            await using var stream = await s3.DownloadAsync(mediaFile.Id.ToString(), ct);
            var isClean = await virusScanner.ScanAsync(stream, ct);

            if (isClean)
                mediaFile.MarkAsActive();
            else
                mediaFile.MarkAsRejected();

            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Another process already started scanning — idempotent
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return Unit.Value;
    }
}
