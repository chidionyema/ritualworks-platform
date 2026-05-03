using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Content.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Logging;
using Haworks.Content.Domain.ValueObjects;
using MediatR;

namespace Haworks.Content.Application.Queries;

public sealed record GetChunkSessionStatusQuery(Guid SessionId) : IRequest<Result<ChunkSession>>;

internal sealed class GetChunkSessionStatusQueryHandler : IRequestHandler<GetChunkSessionStatusQuery, Result<ChunkSession>>
{
    private readonly IChunkedUploadService _chunkedService;
    private readonly ILogger<GetChunkSessionStatusQueryHandler> _logger;

    public GetChunkSessionStatusQueryHandler(
        IChunkedUploadService chunkedService,
        ILogger<GetChunkSessionStatusQueryHandler> logger)
    {
        _chunkedService = chunkedService;
        _logger = logger;
    }

    public async Task<Result<ChunkSession>> Handle(GetChunkSessionStatusQuery request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Retrieving chunk session status. SessionId: {SessionId}", request.SessionId);

        try
        {
            var session = await _chunkedService.GetSessionAsync(request.SessionId);
            return Result.Success(session);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return Result.Failure<ChunkSession>(Error.NotFound("Content.SessionNotFound",
                $"Session {request.SessionId} not found."));
        }
    }
}
