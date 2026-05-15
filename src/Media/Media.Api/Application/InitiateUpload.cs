namespace Haworks.Media.Api.Application;

public record InitiateUploadCommand(string FileName, string Hash, long Size, string MimeType) : IRequest<Result<UploadResponse>>;

public record UploadResponse(Guid Id, string UploadUrl, bool AlreadyExists);

public class InitiateUploadValidator : AbstractValidator<InitiateUploadCommand>
{
    public InitiateUploadValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Hash).NotEmpty().Length(64);
        RuleFor(x => x.Size).GreaterThan(0);
        RuleFor(x => x.MimeType).NotEmpty().MaximumLength(100);
    }
}

public class InitiateUploadHandler : IRequestHandler<InitiateUploadCommand, Result<UploadResponse>>
{
    private readonly MediaDbContext _context;
    private readonly IS3Service _s3Service;

    public InitiateUploadHandler(MediaDbContext context, IS3Service s3Service)
    {
        _context = context;
        _s3Service = s3Service;
    }

    public async Task<Result<UploadResponse>> Handle(InitiateUploadCommand request, CancellationToken cancellationToken)
    {
        // SHA-256 deduplication logic
        var existingFile = await _context.MediaFiles
            .FirstOrDefaultAsync(f => f.Hash == request.Hash, cancellationToken);

        if (existingFile != null)
        {
            return new UploadResponse(existingFile.Id, string.Empty, true);
        }

        var mediaFile = MediaFile.Create(request.FileName, request.Hash, request.Size, request.MimeType);
        
        _context.MediaFiles.Add(mediaFile);
        await _context.SaveChangesAsync(cancellationToken);

        var uploadUrl = _s3Service.GeneratePreSignedUrl(mediaFile.Id.ToString(), mediaFile.MimeType);

        return new UploadResponse(mediaFile.Id, uploadUrl, false);
    }
}
