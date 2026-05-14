using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Extensions;
using Haworks.Content.Application.Commands;
using Haworks.Content.Application.DTOs;
using Haworks.Content.Application.Models;
using Haworks.Content.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Content.Api.Controllers;

/// <summary>
/// Content uploads / reads. Bytes never traverse this server — clients
/// upload directly to S3-compatible storage via presigned URLs minted
/// here, and read via presigned GET URLs returned from the metadata
/// endpoint.
/// </summary>
[ApiController]
[Route("api/v1/content")]
[Authorize(Policy = "ContentUploader")]
public sealed class ContentController(IMediator mediator) : ControllerBase
{
    // ----------------------------------------------------------------
    // Upload pipeline (presigned-URL based; the server is out of the byte path)
    // ----------------------------------------------------------------

    [HttpPost("uploads")]
    [ProducesResponseType(typeof(InitUploadResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> InitUpload(
        [FromBody] InitUploadRequestDto request, CancellationToken ct)
    {
        var ownerId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new InitUploadCommand(
            EntityId: request.EntityId,
            EntityType: request.EntityType,
            FileName: request.FileName,
            ContentType: request.ContentType,
            TotalSize: request.TotalSize,
            OwnerUserId: ownerId), ct);

        if (!result.IsSuccess) return result.ToActionResult();

        return CreatedAtAction(
            nameof(GetUploadStatus),
            new { contentId = result.Value.ContentId },
            result.Value);
    }

    [HttpPost("uploads/{contentId:guid}/complete")]
    [ProducesResponseType(typeof(UploadStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteUpload(
        Guid contentId,
        [FromBody] CompleteUploadRequestDto request,
        CancellationToken ct)
    {
        var ownerId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var parts = request?.Parts?
            .Select(p => new UploadedPart(p.PartNumber, p.ETag))
            .ToArray();

        var result = await mediator.Send(new CompleteUploadCommand(
            ContentId: contentId,
            OwnerUserId: ownerId,
            Parts: parts), ct);

        return result.IsSuccess ? Ok(result.Value) : result.ToActionResult();
    }

    [HttpPost("uploads/{contentId:guid}/abort")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AbortUpload(Guid contentId, CancellationToken ct)
    {
        var ownerId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new AbortUploadCommand(contentId, ownerId), ct);
        return result.ToNoContentActionResult();
    }

    [HttpGet("uploads/{contentId:guid}")]
    [ProducesResponseType(typeof(UploadStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUploadStatus(Guid contentId, CancellationToken ct)
    {
        var ownerId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetUploadStatusQuery(contentId, ownerId), ct);
        return result.IsSuccess ? Ok(result.Value) : result.ToActionResult();
    }

    // ----------------------------------------------------------------
    // Public reads (post-finalisation)
    // ----------------------------------------------------------------

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ContentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetContent(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new GetContentQuery(id), ct);
        return result.IsSuccess ? Ok(result.Value) : result.ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteContent(Guid id, CancellationToken ct)
    {
        var ownerId = HttpContext.GetForwardedUserId();
        if (string.IsNullOrEmpty(ownerId))
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new DeleteContentCommand(id, ownerId), ct);
        return result.ToNoContentActionResult();
    }
}
