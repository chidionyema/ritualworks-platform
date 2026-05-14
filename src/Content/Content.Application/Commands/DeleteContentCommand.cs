using Haworks.BuildingBlocks.Common;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Content.Application.Commands;

public sealed record DeleteContentCommand(Guid ContentId, string OwnerUserId) : IRequest<Result>;

internal sealed class DeleteContentCommandHandler(
    IContentStorageService storageService,
    IContentRepository contentRepository,
    ILogger<DeleteContentCommandHandler> logger) : IRequestHandler<DeleteContentCommand, Result>
{
    public async Task<Result> Handle(DeleteContentCommand request, CancellationToken ct)
    {
        var content = await contentRepository.GetContentByIdTrackedAsync(request.ContentId, ct);
        if (content is null)
        {
            return Result.Failure(Error.Content.NotFound);
        }

        if (content.OwnerUserId != request.OwnerUserId)
        {
            return Result.Failure(Error.Content.Forbidden);
        }

        // Soft-delete the row first; storage delete is best-effort and
        // can be retried by the sweeper if it fails. Doing the soft-delete
        // first means /content/{id} immediately starts returning 404 even
        // if the S3 DELETE call hangs.
        content.SoftDelete();
        await contentRepository.SaveChangesAsync(ct);

        try
        {
            await storageService.DeleteAsync(content.ObjectName, ct);
            logger.LogInformation("Deleted S3 object {ObjectKey}", content.ObjectName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to delete S3 object {ObjectKey}; row marked Deleted, sweeper will retry",
                content.ObjectName);
        }

        return Result.Success();
    }
}
