using Haworks.BffWeb.Api.Demo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// Local-dev only: chaos engineering controls for the live topology map.
/// Visitors click a service / infra node, this controller pauses the
/// underlying process or container so other demos genuinely fail until
/// auto-resume kicks in.
///
/// Production wiring is intentionally absent (the manager is only
/// registered when <c>IsDevelopment()</c>). If somehow reached in
/// production, every endpoint short-circuits to 403.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/demo/chaos")]
[AllowAnonymous]
public sealed class ChaosController : ControllerBase
{
    private readonly ChaosManager? _manager;
    private readonly IWebHostEnvironment _env;

    public ChaosController(IWebHostEnvironment env, ChaosManager? manager = null)
    {
        _env = env;
        _manager = manager;
    }

    [HttpGet("state")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult GetState()
    {
        if (IsForbidden()) return ForbidResponse();
        return Ok(_manager!.Snapshot());
    }

    [HttpPost("{target}/pause")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Pause(string target, [FromBody] PauseRequest? req, CancellationToken ct = default)
    {
        if (IsForbidden()) return ForbidResponse();
        var duration = req?.DurationSeconds is { } s && s > 0
            ? TimeSpan.FromSeconds(s)
            : (TimeSpan?)null;
        var result = await _manager!.PauseAsync(target, duration, ct);
        return result switch
        {
            PauseResult.Ok => Ok(_manager.Snapshot()[target]),
            PauseResult.NotFound => NotFound(new { error = $"Unknown chaos target '{target}'" }),
            _ => StatusCode(500, new { error = "Pause failed; check BFF logs." }),
        };
    }

    [HttpPost("{target}/resume")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Resume(string target, CancellationToken ct = default)
    {
        if (IsForbidden()) return ForbidResponse();
        var resumed = await _manager!.ResumeAsync(target, ct);
        if (!resumed)
        {
            return Ok(new { target, status = "already_running" });
        }
        return Ok(_manager.Snapshot()[target]);
    }

    private bool IsForbidden() => !_env.IsDevelopment() || _manager is null;
    private IActionResult ForbidResponse() =>
        StatusCode(403, new { error = "Chaos controls are dev-mode only." });

    public sealed record PauseRequest(int? DurationSeconds);
}
