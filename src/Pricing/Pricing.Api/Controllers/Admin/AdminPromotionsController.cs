using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Pricing.Api.Controllers.Admin;

/// <summary>
/// Admin endpoints for managing promotion codes.
/// </summary>
[ApiController]
[Route("admin/pricing/promotions")]
[Authorize(Roles = "admin")]
public sealed class AdminPromotionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IPromotionCodeRepository _promoRepo;

    public AdminPromotionsController(IMediator mediator, IPromotionCodeRepository promoRepo)
    {
        _mediator = mediator;
        _promoRepo = promoRepo;
    }

    [HttpPost]
    public async Task<IActionResult> CreatePromotion([FromBody] CreatePromotionCodeCommand command, CancellationToken ct)
    {
        var id = await _mediator.Send(command, ct).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetPromotion), new { id }, new { id });
    }

    [HttpGet]
    public async Task<IActionResult> GetPromotions([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var codes = await _promoRepo.GetAllPagedAsync(page, pageSize, ct).ConfigureAwait(false);
        return Ok(codes);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetPromotion(Guid id, CancellationToken ct)
    {
        var code = await _promoRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (code is null) return NotFound();
        return Ok(code);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeletePromotion(Guid id, CancellationToken ct)
    {
        var code = await _promoRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (code is null) return NotFound();
        code.SoftDelete();
        await _promoRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return NoContent();
    }
}
