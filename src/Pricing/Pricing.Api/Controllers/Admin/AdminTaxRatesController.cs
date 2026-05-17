using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
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
    public async Task<IActionResult> GetTaxRates(CancellationToken ct)
    {
        var rates = await _taxRateRepo.GetAllAsync(ct).ConfigureAwait(false);
        return Ok(rates);
    }

    [HttpPost]
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
        return Created($"/admin/pricing/tax/rates/{rate.Id}", new { id = rate.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTaxRate(Guid id, [FromBody] CreateTaxRateRequest request, CancellationToken ct)
    {
        var existing = await _taxRateRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (existing is null) return NotFound();

        var newRate = TaxRate.Create(
            request.CountryCode, request.StateCode,
            request.CombinedRate, request.StateRate,
            request.CountyRate, request.LocalRate,
            request.EffectiveFrom, request.EffectiveTo,
            request.Notes);

        await _taxRateRepo.AddAsync(newRate, ct).ConfigureAwait(false);
        await _taxRateRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return NoContent();
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
