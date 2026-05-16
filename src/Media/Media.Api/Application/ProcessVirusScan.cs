using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Media.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Media.Api.Application;

public record ProcessVirusScanCommand(Guid MediaId) : IRequest<Result<Unit>>;

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
    private readonly ICurrentUserService _currentUser;
    private readonly IS3Service _s3;

    public ProcessVirusScanHandler(
        MediaDbContext context,
        IVirusScanner virusScanner,
        ICurrentUserService currentUser,
        IS3Service s3)
    {
        _context = context;
        _virusScanner = virusScanner;
        _currentUser = currentUser;
        _s3 = s3;
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

        // Ownership check — only the uploader may trigger a scan on their own file
        if (!string.Equals(mediaFile.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return Result.Failure<MediatR.Unit>(new Error("Media.Forbidden", "You do not own this media file."));
        }

        // Atomically transition state + persist scan result in a single transaction
        await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Mark quarantined while scan is in progress so the file is never
            // considered Active during the window between upload and scan completion.
            mediaFile.MarkAsQuarantined();
            await _context.SaveChangesAsync(cancellationToken);

            // Download the file from S3 before scanning. If download fails, leave the
            // file in Quarantined state and propagate the exception so the transaction rolls back.
            await using var stream = await _s3.DownloadAsync(mediaFile.Id.ToString(), cancellationToken);
            var isClean = await _virusScanner.ScanAsync(stream, cancellationToken);

            if (isClean)
            {
                mediaFile.MarkAsActive();
            }
            else
            {
                mediaFile.MarkAsRejected();
            }

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        return Unit.Value;
    }
}
