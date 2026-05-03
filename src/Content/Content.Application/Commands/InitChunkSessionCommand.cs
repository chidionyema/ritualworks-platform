using Haworks.Content.Application.DTOs;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Content.Application.Interfaces;
using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Logging;
using Haworks.Content.Domain.ValueObjects;
using MediatR;

namespace Haworks.Content.Application.Commands;

public sealed record InitChunkSessionCommand(
    Guid EntityId,
    string FileName,
    string ContentType,
    int TotalChunks,
    long TotalSize,
    int ChunkSize
) : IRequest<Result<ChunkSession>>;

internal sealed class InitChunkSessionCommandHandler : IRequestHandler<InitChunkSessionCommand, Result<ChunkSession>>
{
    private readonly IChunkedUploadService _chunkedService;
    private readonly IFileValidator _fileValidator;
    private readonly ILogger<InitChunkSessionCommandHandler> _logger;

    public InitChunkSessionCommandHandler(
        IChunkedUploadService chunkedService,
        IFileValidator fileValidator,
        ILogger<InitChunkSessionCommandHandler> logger)
    {
        _chunkedService = chunkedService;
        _fileValidator = fileValidator;
        _logger = logger;
    }

    public async Task<Result<ChunkSession>> Handle(InitChunkSessionCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Initializing chunk session. EntityId: {EntityId}, FileName: {FileName}, TotalSize: {TotalSize}",
            request.EntityId, request.FileName, request.TotalSize);

        if (request.TotalChunks <= 0 || request.TotalSize <= 0 || request.ChunkSize <= 0)
        {
            return Result.Failure<ChunkSession>(Error.Content.InvalidChunkParams);
        }

        var validationResult = await _fileValidator.ValidateMetadataAsync(
            request.FileName, request.ContentType, request.TotalSize);

        if (!validationResult.IsValid)
        {
            return Result.Failure<ChunkSession>(
                new Error("Content.MetadataValidationFailed", string.Join(", ", validationResult.Errors), ErrorType.Validation));
        }

        try
        {
            var chunkRequest = new ChunkSessionRequest(
                request.EntityId,
                request.ChunkSize,
                request.FileName,
                request.ContentType,
                request.TotalChunks,
                request.TotalSize
            );

            var session = await _chunkedService.InitSessionAsync(chunkRequest);

            _logger.LogInformation("Chunk session initialized. SessionId: {SessionId}", session.Id);
            return Result.Success(session);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<ChunkSession>(
                new Error("Content.InvalidArgument", ex.Message, ErrorType.Validation));
        }
    }
}
