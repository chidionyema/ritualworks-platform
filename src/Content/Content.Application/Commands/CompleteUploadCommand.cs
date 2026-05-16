using Haworks.BuildingBlocks.Common;
using Haworks.Content.Application.DTOs;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Application.Models;
using Haworks.Content.Application.Options;
using Haworks.Content.Application.Telemetry;
using Haworks.Content.Domain.Entities;
using Haworks.Content.Domain.Interfaces;
using Haworks.Contracts.Content;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.Content.Application.Commands;

/// <summary>
/// Finalises an upload: stitches multipart parts (if any), runs
/// signature + virus + checksum validation, transitions the row to
/// Available or Quarantined. Idempotent: re-completing an Available
/// row is a no-op success.
/// </summary>
public sealed record CompleteUploadCommand(
    Guid ContentId,
    string OwnerUserId,
    IReadOnlyList<UploadedPart>? Parts) : IRequest<Result<UploadStatusDto>>;

internal sealed class CompleteUploadCommandHandler(
    IContentStorageService storage,
    IUploadValidator validator,
    IContentRepository repository,
    IPublishEndpoint publishEndpoint,
    IOptions<StorageOptions> storageOptions,
    ILogger<CompleteUploadCommandHandler> logger,
    TimeProvider time) : IRequestHandler<CompleteUploadCommand, Result<UploadStatusDto>>
{
    public async Task<Result<UploadStatusDto>> Handle(CompleteUploadCommand request, CancellationToken ct)
    {
        using var activity = ContentActivities.Source.StartActivity("content.upload.complete");
        activity?.SetTag("upload.id", request.ContentId);
        activity?.SetTag("upload.owner_id", request.OwnerUserId);
        activity?.SetTag("upload.part_count", request.Parts?.Count ?? 0);

        var content = await repository.GetContentByIdTrackedAsync(request.ContentId, ct);
        if (content is null)
        {
            return Result.Failure<UploadStatusDto>(Error.Content.NotFound);
        }

        if (!string.Equals(content.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal))
        {
            return Result.Failure<UploadStatusDto>(
                new Error("Content.Forbidden", "Caller does not own this upload.", ErrorType.Forbidden));
        }

        // Idempotency: replays of Complete on a finalised row return
        // the existing status, not an error. The IdempotencyMiddleware
        // catches duplicate X-Idempotency-Key submissions earlier; this
        // catches double-completes from buggy clients that miss the
        // 200 response.
        if (content.Status is ContentStatus.Available or ContentStatus.Quarantined or ContentStatus.Failed)
        {
            return Result.Success(ToDto(content));
        }

        if (content.Status != ContentStatus.Pending)
        {
            return Result.Failure<UploadStatusDto>(
                new Error("Content.InvalidState",
                    $"Cannot complete upload in state {content.Status}.", ErrorType.Validation));
        }

        // 1. Stitch (multipart only). Single-PUT uploads land directly in
        // S3, so there's no completion round-trip — we just HEAD to verify.
        try
        {
            if (content.UploadKind == UploadKind.Multipart)
            {
                if (request.Parts is null or { Count: 0 })
                {
                    return Result.Failure<UploadStatusDto>(
                        new Error("Content.InvalidState",
                            "Multipart completions must include the per-part ETag list.",
                            ErrorType.Validation));
                }

                await storage.CompleteMultipartUploadAsync(
                    content.ObjectName, content.S3UploadId!, request.Parts, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Multipart complete failed for {ContentId}", content.Id);
            content.Fail($"S3 CompleteMultipartUpload failed: {ex.Message}");
            await repository.SaveChangesAsync(ct);
            return Result.Success(ToDto(content));
        }

        content.MarkValidating();
        await repository.SaveChangesAsync(ct);

        // 2. Validate. Pulls magic bytes + virus scan + checksum from
        // the object now in S3. The result is authoritative — a failure
        // here moves the object to a quarantine prefix.
        UploadValidationResult verdict;
        try
        {
            verdict = await validator.ValidateAsync(content.ObjectName, content.ContentTypeMime, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation pipeline crashed for {ContentId}", content.Id);
            content.Fail($"Validation pipeline crashed: {ex.Message}");
            await repository.SaveChangesAsync(ct);
            return Result.Success(ToDto(content));
        }

        if (!verdict.IsValid)
        {
            try
            {
                await storage.QuarantineAsync(content.ObjectName, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Quarantine move failed for {ContentId}; row still marked Quarantined",
                    content.Id);
            }
            content.Quarantine(verdict.FailureReason ?? "Validation failed.");
            await publishEndpoint.Publish(new ContentQuarantinedEvent
            {
                ContentId = content.Id,
                EntityId = content.EntityId.ToString(),
                EntityType = content.EntityType,
                OwnerUserId = content.OwnerUserId,
                Reason = verdict.FailureReason ?? "Validation failed."
            }, ct);
            await repository.SaveChangesAsync(ct);
            return Result.Success(ToDto(content));
        }

        var downloadUrl = await storage.GetPresignedGetUrlAsync(
            content.ObjectName, storageOptions.Value.PresignedDownloadTtl, ct);

        content.MarkAvailable(
            etag: verdict.ETag,
            sha256Checksum: verdict.Sha256Checksum,
            actualSize: verdict.SizeBytes,
            url: downloadUrl,
            utcNow: time.GetUtcNow().UtcDateTime);

        await publishEndpoint.Publish(new ContentAvailableEvent
        {
            ContentId = content.Id,
            EntityId = content.EntityId.ToString(),
            EntityType = content.EntityType,
            Slug = content.Slug,
            OwnerUserId = content.OwnerUserId
        }, ct);

        await repository.SaveChangesAsync(ct);

        logger.LogInformation("Upload {ContentId} finalised as Available", content.Id);
        return Result.Success(ToDto(content));
    }

    private static UploadStatusDto ToDto(ContentEntity c) => new(
        ContentId: c.Id,
        Status: c.Status,
        FailureReason: c.FailureReason,
        QuarantineReason: c.QuarantineReason,
        ValidatedAt: c.ValidatedAt);
}
