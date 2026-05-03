using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Content.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Logging;
using Haworks.Content.Domain.Interfaces;
using MediatR;

namespace Haworks.Content.Application.Commands;

public sealed record DeleteContentCommand(Guid ContentId) : IRequest<Result>;

internal sealed class DeleteContentCommandHandler : IRequestHandler<DeleteContentCommand, Result>
{
    private readonly IContentStorageService _storageService;
    private readonly IContentRepository _contentRepository;
    private readonly ILogger<DeleteContentCommandHandler> _logger;

    public DeleteContentCommandHandler(
        IContentStorageService storageService,
        IContentRepository contentRepository,
        ILogger<DeleteContentCommandHandler> logger)
    {
        _storageService = storageService;
        _contentRepository = contentRepository;
        _logger = logger;
    }

    public async Task<Result> Handle(DeleteContentCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to delete content. ContentId: {ContentId}", request.ContentId);

        var content = await _contentRepository.GetContentByIdAsync(request.ContentId, cancellationToken);
        if (content == null)
        {
            return Result.Failure(Error.Content.NotFound);
        }

        // Delete from storage first, then DB
        try
        {
            await _storageService.DeleteAsync(content.BucketName, content.ObjectName, cancellationToken);
            _logger.LogInformation("Deleted file from storage: {BucketName}/{ObjectName}",
                content.BucketName, content.ObjectName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file from storage for ContentId {ContentId}", request.ContentId);
            // Continue with DB deletion even if storage fails
        }

        await _contentRepository.RemoveContentAsync(content, cancellationToken);
        _logger.LogInformation("Content record deleted. ContentId: {ContentId}", request.ContentId);

        return Result.Success();
    }
}
