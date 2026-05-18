using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Contracts.Media;
using Haworks.Media.Api.Infrastructure;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Media.Api.Application;

public record ProcessVirusScanCommand(Guid MediaId, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<Unit>>;

public class ProcessVirusScanValidator : AbstractValidator<ProcessVirusScanCommand>
{
    public ProcessVirusScanValidator()
    {
        RuleFor(x => x.MediaId).NotEmpty();
    }
}

public class ProcessVirusScanHandler : IRequestHandler<ProcessVirusScanCommand, Result<Unit>>
{
    private readonly MediaDbContext _context;
    private readonly IVirusScanner _virusScanner;
    private readonly IFileSignatureValidator _signatureValidator;
    private readonly ICurrentUserService _currentUser;
    private readonly IS3Service _s3;
    private readonly IPublishEndpoint _publisher;
    private readonly ISendEndpointProvider _sendEndpoint;
    private readonly ILogger<ProcessVirusScanHandler> _logger;

    public ProcessVirusScanHandler(
        MediaDbContext context,
        IVirusScanner virusScanner,
        IFileSignatureValidator signatureValidator,
        ICurrentUserService currentUser,
        IS3Service s3,
        IPublishEndpoint publisher,
        ISendEndpointProvider sendEndpoint,
        ILogger<ProcessVirusScanHandler> logger)
    {
        _context = context;
        _virusScanner = virusScanner;
        _signatureValidator = signatureValidator;
        _currentUser = currentUser;
        _s3 = s3;
        _publisher = publisher;
        _sendEndpoint = sendEndpoint;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(ProcessVirusScanCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
        {
            return Result.Failure<MediatR.Unit>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));
        }

        var mediaFile = await _context.MediaFiles
            .FirstOrDefaultAsync(f => f.Id == request.MediaId, cancellationToken);

        if (mediaFile == null)
        {
            return Result.Failure<MediatR.Unit>(new Error("Media.NotFound", "Media file not found."));
        }

        if (!string.Equals(mediaFile.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return Result.Failure<MediatR.Unit>(new Error("Media.Forbidden", "You do not own this media file."));
        }

        // Download to temp file — avoids OOM for large files (up to 256GB).
        var tempPath = Path.Combine(Path.GetTempPath(), $"media-scan-{Guid.NewGuid()}");
        try
        {
            await _s3.DownloadToFileAsync(mediaFile.Id.ToString(), tempPath, cancellationToken);

            // Verify server-side hash matches client-declared hash
            await using (var hashStream = File.OpenRead(tempPath))
            {
                var actualHash = Convert.ToHexStringLower(
                    await System.Security.Cryptography.SHA256.HashDataAsync(hashStream, cancellationToken));
                if (!string.Equals(actualHash, mediaFile.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
                    mediaFile.MarkAsQuarantined();
                    await _context.SaveChangesAsync(cancellationToken);
                    mediaFile.MarkAsRejected();
                    await _context.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return Result.Failure<MediatR.Unit>(new Error("Media.HashMismatch",
                        "Server-side hash does not match declared hash."));
                }
            }

            // Magic-byte signature validation — blocks executables and mismatched MIME types
            await using (var sigStream = File.OpenRead(tempPath))
            {
                var sigResult = await _signatureValidator.ValidateAsync(sigStream, cancellationToken);
                if (!sigResult.IsValid)
                {
                    await _s3.QuarantineAsync(mediaFile.Id.ToString(), cancellationToken);
                    await using var sigTx = await _context.Database.BeginTransactionAsync(cancellationToken);
                    mediaFile.MarkAsQuarantined();
                    await _context.SaveChangesAsync(cancellationToken);
                    mediaFile.MarkAsRejected();
                    await _context.SaveChangesAsync(cancellationToken);
                    await _publisher.Publish(new MediaScanFailedEvent
                    {
                        MediaId = mediaFile.Id,
                        OwnerId = mediaFile.OwnerId,
                        FileName = mediaFile.FileName,
                        Reason = $"File signature mismatch (detected: {sigResult.FileType}).",
                    }, cancellationToken);
                    await sigTx.CommitAsync(cancellationToken);
                    return Result.Failure<MediatR.Unit>(new Error("Media.SignatureMismatch",
                        $"File signature mismatch (detected: {sigResult.FileType})."));
                }
            }

            // Virus scan using file path directly — avoids double temp file for large files
            var isClean = await _virusScanner.ScanFileAsync(tempPath, cancellationToken);

            // All DB writes + event publishes inside a single transaction.
            // With MassTransit EF outbox, Publish() writes to OutboxMessage in the same tx —
            // guarantees atomicity between state change and event delivery.
            await using var tx2 = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                mediaFile.MarkAsQuarantined();
                await _context.SaveChangesAsync(cancellationToken);

                if (isClean)
                {
                    mediaFile.MarkAsActive();
                    await _context.SaveChangesAsync(cancellationToken);

                    // Publish scan-passed event inside the outbox transaction
                    await _publisher.Publish(new MediaScanPassedEvent
                    {
                        MediaId = mediaFile.Id,
                        OwnerId = mediaFile.OwnerId,
                        FileName = mediaFile.FileName,
                        MimeType = mediaFile.MimeType,
                        Size = mediaFile.Size,
                    }, cancellationToken);

                    // Send command point-to-point (not fan-out Publish) — exactly one consumer
                    var endpoint = await _sendEndpoint.GetSendEndpoint(
                        new Uri("queue:process-media-command"));
                    await endpoint.Send(new ProcessMediaCommand
                    {
                        MediaId = mediaFile.Id,
                        OwnerId = mediaFile.OwnerId,
                        FileName = mediaFile.FileName,
                        MimeType = mediaFile.MimeType,
                        S3Key = mediaFile.Id.ToString(),
                    }, cancellationToken);
                }
                else
                {
                    mediaFile.MarkAsRejected();
                    await _context.SaveChangesAsync(cancellationToken);

                    await _publisher.Publish(new MediaScanFailedEvent
                    {
                        MediaId = mediaFile.Id,
                        OwnerId = mediaFile.OwnerId,
                        FileName = mediaFile.FileName,
                        Reason = "Virus detected or scan failed.",
                    }, cancellationToken);
                }

                await tx2.CommitAsync(cancellationToken);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(Handle));
                return Unit.Value;
            }
            catch
            {
                await tx2.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch (Exception ex) { _logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(Handle)); }
        }

        return Unit.Value;
    }
}
