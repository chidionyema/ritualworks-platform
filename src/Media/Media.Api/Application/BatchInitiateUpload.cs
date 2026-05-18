using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Media.Api.Options;
using Microsoft.Extensions.Options;

namespace Haworks.Media.Api.Application;

public record BatchInitiateUploadCommand(IReadOnlyList<InitiateUploadCommand> Files, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<IReadOnlyList<UploadResponse>>>;

public class BatchInitiateUploadValidator : AbstractValidator<BatchInitiateUploadCommand>
{
    public BatchInitiateUploadValidator(IOptions<UploadOptions> opts)
    {
        RuleFor(x => x.Files).NotEmpty()
            .Must(f => f.Count <= 50).WithMessage("Maximum 50 files per batch.");
        RuleForEach(x => x.Files).SetValidator(new InitiateUploadValidator(opts));
    }
}

public class BatchInitiateUploadHandler(
    MediaDbContext context,
    IS3Service s3,
    ICurrentUserService currentUser,
    IOptions<UploadOptions> uploadOpts) : IRequestHandler<BatchInitiateUploadCommand, Result<IReadOnlyList<UploadResponse>>>
{
    private readonly UploadOptions _opts = uploadOpts.Value;

    public async Task<Result<IReadOnlyList<UploadResponse>>> Handle(BatchInitiateUploadCommand request, CancellationToken ct)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId))
            return Result.Failure<IReadOnlyList<UploadResponse>>(new Error("Media.Unauthorized", "Authenticated user identity could not be resolved."));

        var responses = new List<UploadResponse>(request.Files.Count);

        foreach (var file in request.Files)
        {
            var existing = await context.MediaFiles
                .FirstOrDefaultAsync(f => f.Hash == file.Hash && f.OwnerId == ownerId, ct);

            if (existing != null)
            {
                responses.Add(new UploadResponse(existing.Id, null, true));
                continue;
            }

            var mediaFile = MediaFile.Create(file.FileName, file.Hash, file.Size, file.MimeType, ownerId);
            var key = mediaFile.Id.ToString();

            if (file.Size <= _opts.SinglePutMaxBytes)
            {
                context.MediaFiles.Add(mediaFile);
                await context.SaveChangesAsync(ct);
                var uploadUrl = s3.GeneratePreSignedUrl(key, mediaFile.MimeType);
                responses.Add(new UploadResponse(mediaFile.Id, uploadUrl, false));
            }
            else
            {
                var partCount = (int)Math.Ceiling((double)file.Size / _opts.PartSizeBytes);
                var s3UploadId = await s3.InitiateMultipartUploadAsync(key, file.MimeType, ct);
                mediaFile.InitiateMultipart(s3UploadId, partCount);
                context.MediaFiles.Add(mediaFile);
                await context.SaveChangesAsync(ct);

                var partUrls = Enumerable.Range(1, partCount)
                    .Select(i => s3.GeneratePartPresignedUrl(key, s3UploadId, i))
                    .ToList();

                responses.Add(new UploadResponse(mediaFile.Id, null, false, true, s3UploadId, partCount, partUrls));
            }
        }

        return responses;
    }
}
