using Haworks.BuildingBlocks.Common;
using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Pricing.Api.Controllers.Admin;

/// <summary>
/// Admin endpoints for managing tax rates.
/// </summary>
[ApiController]
[Route("admin/pricing/tax/rates")]
[Authorize(Roles = "admin")]
public sealed class AdminTaxRatesController : ControllerBase
{
    private readonly ITaxRateRepository _taxRateRepo;

    public AdminTaxRatesController(ITaxRateRepository taxRateRepo)
    {
        _taxRateRepo = taxRateRepo;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TaxRate>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTaxRates(CancellationToken ct)
    {
        var rates = await _taxRateRepo.GetAllAsync(ct).ConfigureAwait(false);
        return Result.Success(rates).ToActionResult();
    }

    [HttpPost]
    [ProducesResponseType(typeof(object), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateTaxRate([FromBody] CreateTaxRateRequest request, CancellationToken ct)
    {
        var rate = TaxRate.Create(
            request.CountryCode, request.StateCode,
            request.CombinedRate, request.StateRate,
            request.CountyRate, request.LocalRate,
            request.EffectiveFrom, request.EffectiveTo,
            request.Notes);

        await _taxRateRepo.AddAsync(rate, ct).ConfigureAwait(false);
        await _taxRateRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        
        return Result.Success(new { id = rate.Id })
            .ToCreatedActionResult(nameof(GetTaxRates), new { id = rate.Id });
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTaxRate(Guid id, [FromBody] CreateTaxRateRequest request, CancellationToken ct)
    {
        var existing = await _taxRateRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (existing is null) 
            return Result.Failure(Error.NotFound("TaxRate.NotFound", "Tax rate not found")).ToActionResult();

        var effectiveFrom = request.EffectiveFrom ?? DateTimeOffset.UtcNow;

        var newRate = TaxRate.Create(
            request.CountryCode, request.StateCode,
            request.CombinedRate, request.StateRate,
            request.CountyRate, request.LocalRate,
            effectiveFrom, request.EffectiveTo,
            request.Notes);

        // H2 Fix: Expire the old rate when creating a replacement
        existing.SetEffectiveTo(effectiveFrom);

        await _taxRateRepo.AddAsync(newRate, ct).ConfigureAwait(false);
        await _taxRateRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        
        return Result.Success().ToNoContentActionResult();
    }
}

/// <summary>Request body for creating/updating a tax rate.</summary>
public sealed record CreateTaxRateRequest
{
    public required string CountryCode { get; init; }
    public string? StateCode { get; init; }
    public required decimal CombinedRate { get; init; }
    public required decimal StateRate { get; init; }
    public required decimal CountyRate { get; init; }
    public required decimal LocalRate { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public string? Notes { get; init; }
}
