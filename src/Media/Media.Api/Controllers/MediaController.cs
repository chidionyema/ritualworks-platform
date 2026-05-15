using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Media.Api.Controllers;

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

    [HttpPost("{id}/complete")]
    public async Task<IActionResult> CompleteUpload(Guid id)
    {
        // In a real system, this might be triggered by an S3 Event (Lambda)
        // Here we provide an endpoint for the client to signal completion
        var result = await _mediator.Send(new ProcessVirusScanCommand(id));
        return result.ToActionResult();
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetMedia(Guid id, [FromServices] MediaDbContext context)
    {
        var file = await context.MediaFiles.FindAsync(id);
        if (file == null) return NotFound();
        return Ok(file);
    }
}
