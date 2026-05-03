using Microsoft.AspNetCore.Http;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Content.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Logging;
using MediatR;

namespace Haworks.Content.Application.Commands;

public sealed record UploadChunkCommand(
    Guid SessionId,
    int ChunkIndex,
    IFormFile ChunkFile
) : IRequest<Result>;

internal sealed class UploadChunkCommandHandler : IRequestHandler<UploadChunkCommand, Result>
{
    private readonly IChunkedUploadService _chunkedService;
    private readonly ILogger<UploadChunkCommandHandler> _logger;

    public UploadChunkCommandHandler(
        IChunkedUploadService chunkedService,
        ILogger<UploadChunkCommandHandler> logger)
    {
        _chunkedService = chunkedService;
        _logger = logger;
    }

    public async Task<Result> Handle(UploadChunkCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Uploading chunk. SessionId: {SessionId}, ChunkIndex: {ChunkIndex}",
            request.SessionId, request.ChunkIndex);

        if (request.ChunkFile == null || request.ChunkFile.Length == 0)
        {
            return Result.Failure(Error.Content.InvalidChunk);
        }

        if (request.ChunkIndex < 0)
        {
            return Result.Failure(Error.Content.InvalidChunkIndex);
        }

        try
        {
            await using var stream = request.ChunkFile.OpenReadStream();
            await _chunkedService.ProcessChunkAsync(request.SessionId, request.ChunkIndex, stream);

            _logger.LogInformation(
                "Chunk uploaded. SessionId: {SessionId}, ChunkIndex: {ChunkIndex}, Size: {Bytes} bytes",
                request.SessionId, request.ChunkIndex, request.ChunkFile.Length);

            return Result.Success();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Result.Failure(Error.Content.ChunkOutOfRange);
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogError(ex, "Stream disposed during chunk processing");
            return Result.Failure(Error.Content.StreamError);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return Result.Failure(Error.Content.SessionNotFound);
        }
    }
}
