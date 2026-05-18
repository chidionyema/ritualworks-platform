using System.ComponentModel.DataAnnotations;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Media.Api.Options;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Application;

public record InitiateUploadCommand(string FileName, string Hash, [property: System.Text.Json.Serialization.JsonRequired] long Size, string MimeType, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<UploadResponse>>;

public record UploadResponse(
    Guid Id,
    string? UploadUrl,
    bool AlreadyExists,
    bool IsMultipart = false,
    string? S3UploadId = null,
    int PartCount = 0,
    IReadOnlyList<string>? PartUrls = null);

public class InitiateUploadValidator : AbstractValidator<InitiateUploadCommand>
{
    public InitiateUploadValidator(IOptions<UploadOptions> opts)
    {
        var o = opts.Value;
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Hash).NotEmpty().Length(64);
        RuleFor(x => x.Size).GreaterThan(0)
            .LessThanOrEqualTo(o.MaxFileSizeBytes)
            .WithMessage($"File size must not exceed {o.MaxFileSizeBytes / (1024 * 1024 * 1024)}GB.");
        RuleFor(x => x.MimeType).NotEmpty().MaximumLength(100)
            .Must(m => o.AllowedMimeTypes.Count == 0 || o.AllowedMimeTypes.Contains(m))
            .WithMessage("File type is not allowed.");
    }
}

public class InitiateUploadHandler : IRequestHandler<InitiateUploadCommand, Result<UploadResponse>>
{
    private readonly MediaDbContext _context;
    private readonly IS3Service _s3Service;
    private readonly ICurrentUserService _currentUser;
    private readonly UploadOptions _uploadOpts;

    public InitiateUploadHandler(
        MediaDbContext context,
        IS3Service s3Service,
        ICurrentUserService currentUser,
        IOptions<UploadOptions> uploadOpts)
    {
        _context = context;
        _s3Service = s3Service;
        _currentUser = currentUser;
        _uploadOpts = uploadOpts.Value;
    }

    public async Task<Result<UploadResponse>> Handle(InitiateUploadCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
        {
            return Result.Failure<UploadResponse>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));
        }

        var existingFile = await _context.MediaFiles
            .FirstOrDefaultAsync(f => f.Hash == request.Hash && f.OwnerId == ownerId, cancellationToken);

        if (existingFile != null)
        {
            return new UploadResponse(existingFile.Id, null, true);
        }

        var mediaFile = MediaFile.Create(request.FileName, request.Hash, request.Size, request.MimeType, ownerId);
        var key = mediaFile.Id.ToString();

        if (request.Size <= _uploadOpts.SinglePutMaxBytes)
        {
            // Single-part upload
            _context.MediaFiles.Add(mediaFile);
            await _context.SaveChangesAsync(cancellationToken);

            var uploadUrl = _s3Service.GeneratePreSignedUrl(key, mediaFile.MimeType);
            return new UploadResponse(mediaFile.Id, uploadUrl, false);
        }

        // Multipart upload
        var partCount = (int)Math.Ceiling((double)request.Size / _uploadOpts.PartSizeBytes);
        if (partCount > 10_000)
        {
            return Result.Failure<UploadResponse>(new Error("Media.TooManyParts",
                $"File requires {partCount} parts but S3 allows max 10,000. Increase part size."));
        }

        var s3UploadId = await _s3Service.InitiateMultipartUploadAsync(key, request.MimeType, cancellationToken);
        mediaFile.InitiateMultipart(s3UploadId, partCount);

        _context.MediaFiles.Add(mediaFile);
        await _context.SaveChangesAsync(cancellationToken);

        var partUrls = Enumerable.Range(1, partCount)
            .Select(i => _s3Service.GeneratePartPresignedUrl(key, s3UploadId, i))
            .ToList();

        return new UploadResponse(mediaFile.Id, null, false, true, s3UploadId, partCount, partUrls);
    }
}
