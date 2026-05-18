using System.Security.Claims;
using Haworks.BuildingBlocks.Common;
using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Application.Queries;
using Haworks.Pricing.Domain.Exceptions;
using Haworks.Pricing.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Haworks.Pricing.Api.Controllers;

/// <summary>
/// Public pricing endpoints: calculate price, validate/redeem promotions.
/// </summary>
[ApiController]
[Route("pricing")]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status500InternalServerError)]
public sealed class PricingController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly BrandOptions _brandOptions;

    public PricingController(IMediator mediator, IOptions<BrandOptions> brandOptions)
    {
        _mediator = mediator;
        _brandOptions = brandOptions.Value;
    }

    /// <summary>
    /// Calculate effective price for a product.
    /// </summary>
    [HttpGet("calculate")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PriceBreakdownResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Calculate(
        [FromQuery] Guid productId,
        [FromQuery] int quantity,
        [FromQuery] string? promoCode = null,
        [FromQuery] string? userId = null,
        [FromQuery] string? countryCode = null,
        [FromQuery] string? stateCode = null,
        CancellationToken ct = default)
    {
        if (productId == Guid.Empty)
            return BadRequest("productId is required.");

        if (quantity < 1 || quantity > 9999)
            return BadRequest("Quantity must be between 1 and 9999.");

        var result = await _mediator.Send(new CalculateEffectivePriceQuery
        {
            ProductId = productId,
            Quantity = quantity,
            PromoCode = promoCode,
            UserId = userId,
            CountryCode = countryCode,
            StateCode = stateCode,
        }, ct).ConfigureAwait(false);

        return Result.Success(result).ToActionResult();
    }

    /// <summary>
    /// Validate a promotion code without redeeming.
    /// </summary>
    [HttpPost("promotions/validate")]
    [ProducesResponseType(typeof(ValidatePromotionCodeResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidatePromotion(
        [FromBody] ValidatePromotionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.ProductId == Guid.Empty)
            return BadRequest("code and productId are required.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier); // from JWT
        var result = await _mediator.Send(new ValidatePromotionCodeQuery
        {
            Code = request.Code,
            ProductId = request.ProductId,
            UserId = userId,
        }, ct).ConfigureAwait(false);

        return Result.Success(result).ToActionResult();
    }

    /// <summary>
    /// Redeem a promotion code (idempotent by orderId).
    /// </summary>
    [HttpPost("promotions/redeem")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RedeemPromotion(
        [FromBody] RedeemPromotionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.OrderId == Guid.Empty)
            return BadRequest("code and orderId are required.");

        var redeemUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!; // from JWT
        var result = await _mediator.Send(new RedeemPromotionCodeCommand
        {
            Code = request.Code,
            OrderId = request.OrderId,
            UserId = redeemUserId,
            DiscountAmount = request.DiscountAmount,
            CalculationId = request.CalculationId,
        }, ct).ConfigureAwait(false);

        return Result.Success(result).ToActionResult();
    }

    /// <summary>
    /// Get tax rate for a jurisdiction.
    /// </summary>
    [HttpGet("tax/rate")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(TaxRateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTaxRate(
        [FromQuery] string countryCode,
        [FromQuery] string? stateCode,
        [FromServices] Application.Interfaces.ITaxCalculator taxCalculator,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return BadRequest("countryCode is required.");

        var result = await taxCalculator.CalculateAsync(
            countryCode, 
            stateCode, 
            100m, 
            _brandOptions.DefaultCurrency, 
            ct).ConfigureAwait(false);

        return Ok(new TaxRateResponse
        {
            CountryCode = countryCode,
            StateCode = stateCode,
            CombinedRate = result.EffectiveRate,
            Source = result.Source,
        });
    }
}

public sealed record TaxRateResponse
{
    public required string CountryCode { get; init; }
    public string? StateCode { get; init; }
    public decimal CombinedRate { get; init; }
    public string Source { get; init; } = string.Empty;
}


/// <summary>Request body for promotion validation.</summary>
public sealed record ValidatePromotionRequest
{
    public required string Code { get; init; }
    public required Guid ProductId { get; init; }
    public string? UserId { get; init; }
}

/// <summary>Request body for promotion redemption.</summary>
public sealed record RedeemPromotionRequest
{
    public required string Code { get; init; }
    public required Guid OrderId { get; init; }
    public string? UserId { get; init; }
    public required decimal DiscountAmount { get; init; }
    public required Guid CalculationId { get; init; }
}
