using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Media.Api.Controllers;

/// <summary>
/// Response DTO — shields the API surface from internal entity shape changes
/// and prevents accidental serialisation of EF navigation properties.
/// </summary>
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
public class MediaController : ControllerBase
{
    private readonly IMediator _mediator;

    public MediaController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("initiate")]
    public async Task<IActionResult> InitiateUpload([FromBody] InitiateUploadCommand command)
    {
        var result = await _mediator.Send(command);
        return result.ToActionResult();
    }

    /// <summary>
    /// Called by the client after the direct S3 PUT upload completes.
    /// Triggers the virus scan pipeline for the media file.
    /// </summary>
    [HttpPost("{id}/complete")]
    public async Task<IActionResult> CompleteUpload(Guid id)
    {
        var result = await _mediator.Send(new ProcessVirusScanCommand(id));
        return result.ToActionResult();
    }

    /// <summary>
    /// Returns media file metadata as a DTO — never the raw entity.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetMedia(Guid id, [FromServices] MediaDbContext context)
    {
        var file = await context.MediaFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id);

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
}
