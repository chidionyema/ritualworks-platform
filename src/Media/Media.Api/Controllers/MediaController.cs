using Microsoft.AspNetCore.Authorization;
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
    public async Task<IActionResult> InitiateUpload([FromBody] InitiateUploadCommand command)
    {
        var result = await mediator.Send(command);
        return result.ToActionResult();
    }

    /// <summary>
    /// Called by the client after a single-part S3 PUT upload completes.
    /// Triggers the virus scan pipeline.
    /// </summary>
    [HttpPost("{id}/complete")]
    public async Task<IActionResult> CompleteUpload(Guid id)
    {
        var result = await mediator.Send(new ProcessVirusScanCommand(id));
        return result.ToActionResult();
    }

    /// <summary>
    /// Called by the client after all multipart parts are uploaded.
    /// Stitches parts in S3 and triggers the virus scan pipeline.
    /// </summary>
    [HttpPost("{id}/complete-multipart")]
    public async Task<IActionResult> CompleteMultipartUpload(Guid id, [FromBody] CompleteMultipartRequest body)
    {
        var result = await mediator.Send(new CompleteMultipartUploadCommand(id, body.Parts));
        return result.ToActionResult();
    }

    /// <summary>
    /// Aborts an in-progress upload. For multipart uploads, also aborts the S3 multipart upload.
    /// </summary>
    [HttpPost("{id}/abort")]
    public async Task<IActionResult> AbortUpload(Guid id)
    {
        var result = await mediator.Send(new AbortUploadCommand(id));
        return result.ToActionResult();
    }

    /// <summary>
    /// Returns media file metadata as a DTO — never the raw entity.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetMedia(
        Guid id,
        [FromServices] MediaDbContext context,
        [FromServices] Haworks.BuildingBlocks.CurrentUser.ICurrentUserService currentUser)
    {
        var ownerId = currentUser.UserId;
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();

        var file = await context.MediaFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.OwnerId == ownerId);

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
    public async Task<IActionResult> ListMedia(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? mimeTypePrefix = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await mediator.Send(new ListMediaQuery(page, pageSize, status, mimeTypePrefix));
        return result.ToActionResult();
    }

    /// <summary>
    /// Returns a presigned GET URL for downloading the original or a processed variant.
    /// </summary>
    [HttpGet("{id}/url")]
    public async Task<IActionResult> GetMediaUrl(Guid id, [FromQuery] string? variant = null)
    {
        var result = await mediator.Send(new GetMediaUrlQuery(id, variant));
        return result.ToActionResult();
    }
}

public sealed record CompleteMultipartRequest(IReadOnlyList<PartETagDto> Parts);
