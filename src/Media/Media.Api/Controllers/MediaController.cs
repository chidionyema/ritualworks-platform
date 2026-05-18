using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Media.Api.Controllers;

public sealed record MediaFileResponse(
    Guid Id,
    string FileName,
    string MimeType,
    long Size,
    string Status,
    DateTime CreatedAt);

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class MediaController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Initiates a media upload. Returns presigned PUT URL for single-part uploads,
    /// or S3 upload ID + per-part presigned URLs for multipart uploads (files > 8MB).
    /// </summary>
    [HttpPost("initiate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> InitiateUpload([FromBody] InitiateUploadCommand command, CancellationToken ct = default)
    {
        var result = await mediator.Send(command, ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Called by the client after a single-part S3 PUT upload completes.
    /// Triggers the virus scan pipeline.
    /// </summary>
    [HttpPost("{id}/complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompleteUpload(Guid id, CancellationToken ct = default)
    {
        var result = await mediator.Send(new ProcessVirusScanCommand(id), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Called by the client after all multipart parts are uploaded.
    /// Stitches parts in S3 and triggers the virus scan pipeline.
    /// </summary>
    [HttpPost("{id}/complete-multipart")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompleteMultipartUpload(Guid id, [FromBody] CompleteMultipartRequest body, CancellationToken ct = default)
    {
        var result = await mediator.Send(new CompleteMultipartUploadCommand(id, body.Parts), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Aborts an in-progress upload. For multipart uploads, also aborts the S3 multipart upload.
    /// </summary>
    [HttpPost("{id}/abort")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AbortUpload(Guid id, CancellationToken ct = default)
    {
        var result = await mediator.Send(new AbortUploadCommand(id), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Returns media file metadata as a DTO — never the raw entity.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMedia(
        Guid id,
        [FromServices] MediaDbContext context,
        [FromServices] Haworks.BuildingBlocks.CurrentUser.ICurrentUserService currentUser,
        CancellationToken ct = default)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();

        var file = await context.MediaFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == ownerId, ct);

        if (file == null) return NotFound();

        var response = new MediaFileResponse(
            file.Id,
            file.FileName,
            file.MimeType,
            file.Size,
            file.Status.ToString(),
            file.CreatedAt);

        return Ok(response);
    }

    /// <summary>
    /// Lists the caller's media files with pagination and optional filtering.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListMedia(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? mimeTypePrefix = null,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await mediator.Send(new ListMediaQuery(page, pageSize, status, mimeTypePrefix), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Returns a presigned GET URL for downloading the original or a processed variant.
    /// Client downloads directly from S3 — no bandwidth through the API server.
    /// </summary>
    [HttpGet("{id}/url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMediaUrl(Guid id, [FromQuery] string? variant = null, CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetMediaUrlQuery(id, variant), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Batch-initiate multiple uploads in a single request.
    /// Returns presigned URLs for each file (single-part or multipart).
    /// </summary>
    [HttpPost("batch-initiate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BatchInitiateUpload([FromBody] BatchInitiateUploadCommand command, CancellationToken ct = default)
    {
        var result = await mediator.Send(command, ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Links a media file to a domain entity (e.g. product, post).
    /// </summary>
    [HttpPost("{id}/link")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LinkEntity(Guid id, [FromBody] LinkEntityRequest body, CancellationToken ct = default)
    {
        var result = await mediator.Send(new LinkEntityCommand(id, body.EntityId, body.EntityType), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Soft-deletes a media file. Bytes remain in S3 pending GC.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteMedia(Guid id, CancellationToken ct = default)
    {
        var result = await mediator.Send(new DeleteMediaCommand(id), ct);
        return result.ToActionResult();
    }
}

public sealed record CompleteMultipartRequest(IReadOnlyList<PartETagDto> Parts);
public sealed record LinkEntityRequest([property: System.Text.Json.Serialization.JsonRequired] Guid EntityId, string EntityType);
