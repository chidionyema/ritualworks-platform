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
    [ProducesResponseType(typeof(object), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreatePromotion([FromBody] CreatePromotionCodeCommand command, CancellationToken ct)
    {
        var id = await _mediator.Send(command, ct).ConfigureAwait(false);
        return Result.Success(new { id }).ToCreatedActionResult(nameof(GetPromotion), new { id });
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PromotionCode>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPromotions([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var codes = await _promoRepo.GetAllPagedAsync(page, pageSize, ct).ConfigureAwait(false);
        return Result.Success(codes).ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PromotionCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPromotion(Guid id, CancellationToken ct)
    {
        var code = await _promoRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (code is null) 
            return Result.Failure<PromotionCode>(Error.NotFound("PromotionCode.NotFound", "Promotion code not found")).ToActionResult();
            
        return Result.Success(code).ToActionResult();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePromotion(Guid id, CancellationToken ct)
    {
        var code = await _promoRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (code is null) 
            return Result.Failure(Error.NotFound("PromotionCode.NotFound", "Promotion code not found")).ToActionResult();
            
        code.SoftDelete();
        await _promoRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success().ToNoContentActionResult();
    }
}
