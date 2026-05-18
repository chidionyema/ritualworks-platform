using Haworks.BuildingBlocks.Common;
using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    [ProducesResponseType(typeof(object), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateRule([FromBody] CreatePriceRuleCommand command, CancellationToken ct)
    {
        var id = await _mediator.Send(command, ct).ConfigureAwait(false);
        return Result.Success(new { id }).ToCreatedActionResult(nameof(GetRule), new { id });
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PriceRule>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRules([FromQuery] Guid? productId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var rules = await _ruleRepo.GetAllPagedAsync(productId, page, pageSize, ct).ConfigureAwait(false);
        return Result.Success(rules).ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PriceRule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken ct)
    {
        var rule = await _ruleRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (rule is null) 
            return Result.Failure<PriceRule>(Error.NotFound("PriceRule.NotFound", "Price rule not found")).ToActionResult();
        
        return Result.Success(rule).ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        var rule = await _ruleRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (rule is null) 
            return Result.Failure(Error.NotFound("PriceRule.NotFound", "Price rule not found")).ToActionResult();
        
        rule.Archive();
        await _ruleRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success().ToNoContentActionResult();
    }
}
