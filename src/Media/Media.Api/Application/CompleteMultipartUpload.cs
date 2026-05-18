using Amazon.S3.Model;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Idempotency;
using MassTransit;

namespace Haworks.Media.Api.Application;

public record CompleteMultipartUploadCommand(Guid MediaId, IReadOnlyList<PartETagDto> Parts, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<Unit>>;
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
    ICurrentUserService currentUser,
    IPublishEndpoint publisher,
    ISendEndpointProvider sendEndpoint,
    ILogger<CompleteMultipartUploadHandler> logger) : IRequestHandler<CompleteMultipartUploadCommand, Result<Unit>>
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

        // Download to temp file to avoid OOM for large files
        var tempPath = Path.Combine(Path.GetTempPath(), $"media-scan-{Guid.NewGuid()}");
        try
        {
            await s3.DownloadToFileAsync(mediaFile.Id.ToString(), tempPath, ct);

            // Verify server-side hash matches client-declared hash
            await using (var hashStream = File.OpenRead(tempPath))
            {
                var actualHash = Convert.ToHexStringLower(
                    await System.Security.Cryptography.SHA256.HashDataAsync(hashStream, ct));
                if (!string.Equals(actualHash, mediaFile.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    await using var hashTx = await context.Database.BeginTransactionAsync(ct);
                    mediaFile.MarkAsQuarantined();
                    await context.SaveChangesAsync(ct);
                    mediaFile.MarkAsRejected();
                    await context.SaveChangesAsync(ct);
                    await hashTx.CommitAsync(ct);
                    return Result.Failure<Unit>(new Error("Media.HashMismatch",
                        "Server-side hash does not match declared hash."));
                }
            }

            var isClean = await virusScanner.ScanFileAsync(tempPath, ct);

            // All DB writes + event publishes inside a single transaction (outbox atomicity)
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            try
            {
                mediaFile.MarkAsQuarantined();
                await context.SaveChangesAsync(ct);

                if (isClean)
                {
                    mediaFile.MarkAsActive();
                    await context.SaveChangesAsync(ct);

                    await publisher.Publish(new Haworks.Contracts.Media.MediaScanPassedEvent
                    {
                        MediaId = mediaFile.Id,
                        OwnerId = mediaFile.OwnerId,
                        FileName = mediaFile.FileName,
                        MimeType = mediaFile.MimeType,
                        Size = mediaFile.Size,
                    }, ct);

                    var endpoint = await sendEndpoint.GetSendEndpoint(
                        new Uri("queue:process-media-command"));
                    await endpoint.Send(new Haworks.Contracts.Media.ProcessMediaCommand
                    {
                        MediaId = mediaFile.Id,
                        OwnerId = mediaFile.OwnerId,
                        FileName = mediaFile.FileName,
                        MimeType = mediaFile.MimeType,
                        S3Key = mediaFile.Id.ToString(),
                    }, ct);
                }
                else
                {
                    mediaFile.MarkAsRejected();
                    await context.SaveChangesAsync(ct);

                    await publisher.Publish(new Haworks.Contracts.Media.MediaScanFailedEvent
                    {
                        MediaId = mediaFile.Id,
                        OwnerId = mediaFile.OwnerId,
                        FileName = mediaFile.FileName,
                        Reason = "Virus detected or scan failed.",
                    }, ct);
                }

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
        }
        finally
        {
            try { File.Delete(tempPath); } catch (Exception ex) { logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(Handle)); }
        }

        return Unit.Value;
    }
}
