using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediatR;
using Haworks.Content.Api.Models;
using Haworks.Content.Application.Commands;
using Haworks.Content.Application.Queries;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Extensions;

namespace Haworks.Content.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Policy = "ContentUploader")]
public class ContentController : ControllerBase
{
    private readonly IMediator _mediator;

    public ContentController(IMediator mediator)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(100_000_000)]
    [ProducesResponseType(typeof(ContentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadFile([FromQuery] Guid entityId, [FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new UploadFileCommand(entityId, file, User.GetUserId() ?? "unknown"), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new ContentResponse(dto.Id, dto.EntityId, dto.EntityType, dto.Url, dto.ContentType, dto.FileSize);
        return CreatedAtAction(nameof(GetContent), new { id = dto.Id }, response);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ContentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetContent(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetContentQuery(id), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new ContentResponse(dto.Id, dto.EntityId, dto.EntityType, dto.Url, dto.ContentType, dto.FileSize);
        return Ok(response);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteContent(Guid id, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new DeleteContentCommand(id), cancellationToken);
        return result.ToNoContentActionResult();
    }

    [HttpPost("chunked/init")]
    [RequestSizeLimit(10_000)]
    [ProducesResponseType(typeof(ChunkSessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> InitChunkSession([FromBody] InitChunkSessionRequest request, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new InitChunkSessionCommand(
            request.EntityId,
            request.FileName,
            request.ContentType,
            request.TotalChunks,
            request.TotalSize,
            request.ChunkSize
        ), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var session = result.Value;
        var response = new ChunkSessionResponse(session.Id, session.ExpiresAt, session.TotalChunks);
        return CreatedAtAction(nameof(GetChunkSessionStatus), new { sessionId = session.Id }, response);
    }

    [HttpPost("chunked/{sessionId}/{chunkIndex}")]
    [RequestSizeLimit(11_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11_000_000)]
    public async Task<IActionResult> UploadChunk(Guid sessionId, int chunkIndex, IFormFile chunkFile, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new UploadChunkCommand(sessionId, chunkIndex, chunkFile), cancellationToken);

        if (result.IsSuccess)
            return Ok(new { message = $"Chunk {chunkIndex} for session {sessionId} uploaded successfully." });

        return result.ToActionResult();
    }

    [HttpPost("chunked/complete/{sessionId}")]
    [ProducesResponseType(typeof(ContentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteChunkSession(Guid sessionId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new CompleteChunkSessionCommand(sessionId, User.GetUserId() ?? "unknown"), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var dto = result.Value;
        var response = new ContentResponse(dto.Id, dto.EntityId, dto.EntityType, dto.Url, dto.ContentType, dto.FileSize);
        return CreatedAtAction(nameof(GetContent), new { id = dto.Id }, response);
    }

    [HttpGet("chunked/session/{sessionId}")]
    [ProducesResponseType(typeof(ChunkSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetChunkSessionStatus(Guid sessionId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetChunkSessionStatusQuery(sessionId), cancellationToken);

        if (!result.IsSuccess)
            return result.ToActionResult();

        var session = result.Value;
        var response = new ChunkSessionResponse(session.Id, session.ExpiresAt, session.TotalChunks);
        return Ok(response);
    }
}
