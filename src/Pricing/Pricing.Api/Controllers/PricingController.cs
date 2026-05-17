using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Application.Queries;
using Haworks.Pricing.Domain.Exceptions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Pricing.Api.Controllers;

/// <summary>
/// Public pricing endpoints: calculate price, validate/redeem promotions.
/// </summary>
[ApiController]
[Route("pricing")]
public sealed class PricingController : ControllerBase
{
    private readonly IMediator _mediator;

    public PricingController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Calculate effective price for a product.
    /// </summary>
    [HttpGet("calculate")]
    [AllowAnonymous]
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

        try
        {
            var result = await _mediator.Send(new CalculateEffectivePriceQuery
            {
                ProductId = productId,
                Quantity = quantity,
                PromoCode = promoCode,
                UserId = userId,
                CountryCode = countryCode,
                StateCode = stateCode,
            }, ct).ConfigureAwait(false);

            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound(new { error = ex.Message });
        }
        catch (TaxCalculationException ex)
        {
            return StatusCode(500, new { error = "Tax calculation failed", detail = ex.Message });
        }
    }

    /// <summary>
    /// Validate a promotion code without redeeming.
    /// </summary>
    [HttpPost("promotions/validate")]
    public async Task<IActionResult> ValidatePromotion(
        [FromBody] ValidatePromotionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.ProductId == Guid.Empty)
            return BadRequest("code and productId are required.");

        var result = await _mediator.Send(new ValidatePromotionCodeQuery
        {
            Code = request.Code,
            ProductId = request.ProductId,
            UserId = request.UserId,
        }, ct).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>
    /// Redeem a promotion code (idempotent by orderId).
    /// </summary>
    [HttpPost("promotions/redeem")]
    [Authorize]
    public async Task<IActionResult> RedeemPromotion(
        [FromBody] RedeemPromotionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.OrderId == Guid.Empty)
            return BadRequest("code and orderId are required.");

        var result = await _mediator.Send(new RedeemPromotionCodeCommand
        {
            Code = request.Code,
            OrderId = request.OrderId,
            UserId = request.UserId,
            DiscountAmount = request.DiscountAmount,
            CalculationId = request.CalculationId,
        }, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return result.FailureReason switch
            {
                "exhausted" => Conflict(new { error = "Promotion code exhausted." }),
                "expired" => UnprocessableEntity(new { error = "Promotion code expired." }),
                _ => UnprocessableEntity(new { error = $"Promotion code invalid: {result.FailureReason}" }),
            };
        }

        return NoContent();
    }

    /// <summary>
    /// Get tax rate for a jurisdiction.
    /// </summary>
    [HttpGet("tax/rate")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTaxRate(
        [FromQuery] string countryCode,
        [FromQuery] string? stateCode,
        [FromServices] Application.Interfaces.ITaxCalculator taxCalculator,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return BadRequest("countryCode is required.");

        try
        {
            var result = await taxCalculator.CalculateAsync(countryCode, stateCode, 100m, "USD", ct).ConfigureAwait(false);
            return Ok(new
            {
                countryCode,
                stateCode,
                combinedRate = result.EffectiveRate,
                source = result.Source,
            });
        }
        catch (TaxCalculationException)
        {
            return NotFound(new { error = $"No tax rate configured for {countryCode}/{stateCode}" });
        }
    }
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
