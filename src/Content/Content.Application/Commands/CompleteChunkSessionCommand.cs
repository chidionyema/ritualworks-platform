using System.Security;
using Haworks.Content.Application.DTOs;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Content.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Logging;
using MediatR;

namespace Haworks.Content.Application.Commands;

public sealed record CompleteChunkSessionCommand(
    Guid SessionId,
    string UserId
) : IRequest<Result<ContentDto>>;

internal sealed class CompleteChunkSessionCommandHandler : IRequestHandler<CompleteChunkSessionCommand, Result<ContentDto>>
{
    private readonly IChunkedUploadService _chunkedService;
    private readonly ILogger<CompleteChunkSessionCommandHandler> _logger;

    public CompleteChunkSessionCommandHandler(
        IChunkedUploadService chunkedService,
        ILogger<CompleteChunkSessionCommandHandler> logger)
    {
        _chunkedService = chunkedService;
        _logger = logger;
    }

    public async Task<Result<ContentDto>> Handle(CompleteChunkSessionCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Completing chunk session. SessionId: {SessionId}", request.SessionId);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            var content = await _chunkedService.CompleteSessionAsync(request.SessionId, request.UserId);

            if (content == null)
            {
                return Result.Failure<ContentDto>(Error.Content.CompletionFailed);
            }

            _logger.LogInformation("Chunk session completed. SessionId: {SessionId}, ContentId: {ContentId}",
                request.SessionId, content.Id);

            return Result.Success(new ContentDto(
                content.Id,
                content.EntityId,
                content.EntityType,
                content.Path,
                content.ContentType.ToString(),
                content.FileSize));
        }
        catch (KeyNotFoundException)
        {
            return Result.Failure<ContentDto>(Error.Content.SessionNotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<ContentDto>(
                new Error("Content.InvalidOperation", ex.Message, ErrorType.Validation));
        }
        catch (TimeoutException)
        {
            return Result.Failure<ContentDto>(Error.Timeout("Content.Timeout", "Session completion timed out."));
        }
        catch (SecurityException)
        {
            return Result.Failure<ContentDto>(Error.Content.Forbidden);
        }
    }
}
