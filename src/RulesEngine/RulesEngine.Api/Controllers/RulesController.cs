using Haworks.RulesEngine.Api.Application;
using Haworks.BuildingBlocks.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
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
    public async Task<IActionResult> Evaluate([FromBody] EvaluateRuleQuery query)
    {
        var result = await _mediator.Send(query);
        return result.ToActionResult();
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool? activeOnly = null)
    {
        var result = await _mediator.Send(new ListRulesQuery(activeOnly));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var result = await _mediator.Send(new GetRuleQuery(id));
        return result.ToActionResult();
    }

    [Authorize(Roles = "admin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRuleCommand command)
    {
        var result = await _mediator.Send(command);
        if (!result.IsSuccess) return result.ToActionResult();
        return CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value);
    }

    [Authorize(Roles = "admin")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRuleCommand command)
    {
        if (id != command.Id)
            return BadRequest(new { error = "Route id does not match body id." });

        var result = await _mediator.Send(command);
        return result.ToActionResult();
    }

    [Authorize(Roles = "admin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediator.Send(new DeleteRuleCommand(id));
        return result.ToNoContentActionResult();
    }
}
