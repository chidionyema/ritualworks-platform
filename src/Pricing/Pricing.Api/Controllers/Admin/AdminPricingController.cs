using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Pricing.Api.Controllers.Admin;

/// <summary>
/// Admin endpoints for managing pricing rules.
/// Requires admin role claim (IDOR protection).
/// </summary>
[ApiController]
[Route("admin/pricing/rules")]
[Authorize(Roles = "admin")]
public sealed class AdminPriceRulesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IPriceRuleRepository _ruleRepo;

    public AdminPriceRulesController(IMediator mediator, IPriceRuleRepository ruleRepo)
    {
        _mediator = mediator;
        _ruleRepo = ruleRepo;
    }

    [HttpPost]
    public async Task<IActionResult> CreateRule([FromBody] CreatePriceRuleCommand command, CancellationToken ct)
    {
        var id = await _mediator.Send(command, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetRule), new { id }, new { id });
    }

    [HttpGet]
    public async Task<IActionResult> GetRules([FromQuery] Guid? productId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var rules = await _ruleRepo.GetAllPagedAsync(productId, page, pageSize, ct).ConfigureAwait(false);
        return Ok(rules);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken ct)
    {
        var rule = await _ruleRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (rule is null) return NotFound();
        return Ok(rule);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        var rule = await _ruleRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (rule is null) return NotFound();
        rule.Archive();
        await _ruleRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return NoContent();
    }
}
