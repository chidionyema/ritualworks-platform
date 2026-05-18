using Haworks.RulesEngine.Api.Application;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.RulesEngine.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class RulesController : ControllerBase
{
    private readonly IMediator _mediator;

    public RulesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    // ── Evaluation ────────────────────────────────────────────────────────────

    [HttpPost("evaluate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Evaluate([FromBody] EvaluateRuleQuery query, CancellationToken ct = default)
    {
        var result = await _mediator.Send(query, ct);
        return result.ToActionResult();
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] bool? activeOnly = null, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new ListRulesQuery(activeOnly), ct);
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetRuleQuery(id), ct);
        return result.ToActionResult();
    }

    [Authorize(Roles = "admin")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateRuleCommand command, CancellationToken ct = default)
    {
        var result = await _mediator.Send(command, ct);
        if (!result.IsSuccess) return result.ToActionResult();
        return CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value);
    }

    [Authorize(Roles = "admin")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRuleCommand command, CancellationToken ct = default)
    {
        if (id != command.Id)
            return BadRequest(new { error = "Route id does not match body id." });

        var result = await _mediator.Send(command, ct);
        return result.ToActionResult();
    }

    [Authorize(Roles = "admin")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new DeleteRuleCommand(id), ct);
        return result.ToNoContentActionResult();
    }
}
