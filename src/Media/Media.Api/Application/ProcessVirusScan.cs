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

    public ProcessVirusScanHandler(MediaDbContext context)
    {
        _context = context;
    }

    public async Task<Result<Unit>> Handle(ProcessVirusScanCommand request, CancellationToken cancellationToken)
    {
        var mediaFile = await _context.MediaFiles.FindAsync(new object[] { request.MediaId }, cancellationToken);

        if (mediaFile == null)
        {
            return Result.Failure<MediatR.Unit>(new Error("Media.NotFound", "Media file not found"));
        }

        mediaFile.MarkAsQuarantined();
        await _context.SaveChangesAsync(cancellationToken);

        // Mock virus scan
        await Task.Delay(100, cancellationToken); // Simulating work

        // For this demo, we always pass
        mediaFile.MarkAsActive();
        await _context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
