using System.Text.Json;
using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Application.Services;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Haworks.Pricing.Application.Queries;

/// <summary>
/// Handles price calculation following the spec's 10-step pipeline.
/// </summary>
public sealed class CalculateEffectivePriceQueryHandler : IRequestHandler<CalculateEffectivePriceQuery, PriceBreakdownResult>
{
    private readonly ICatalogPricingClient _catalogClient;
    private readonly IPriceRuleRepository _priceRuleRepo;
    private readonly IPromotionCodeRepository _promoCodeRepo;
    private readonly ITaxCalculator _taxCalculator;
    private readonly ICalculationLogRepository _logRepo;
    private readonly PriceCalculationEngine _engine;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CalculateEffectivePriceQueryHandler> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public CalculateEffectivePriceQueryHandler(
        ICatalogPricingClient catalogClient,
        IPriceRuleRepository priceRuleRepo,
        IPromotionCodeRepository promoCodeRepo,
        ITaxCalculator taxCalculator,
        ICalculationLogRepository logRepo,
        PriceCalculationEngine engine,
        IMemoryCache cache,
        ILogger<CalculateEffectivePriceQueryHandler> logger)
    {
        _catalogClient = catalogClient;
        _priceRuleRepo = priceRuleRepo;
        _promoCodeRepo = promoCodeRepo;
        _taxCalculator = taxCalculator;
        _logRepo = logRepo;
        _engine = engine;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PriceBreakdownResult> Handle(CalculateEffectivePriceQuery request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Step 1: Fetch base price from catalog (60s cache)
        var cacheKey = $"catalog_price_{request.ProductId}";
        if (!_cache.TryGetValue(cacheKey, out Models.CatalogProductDto? product))
        {
            var response = await _catalogClient.GetProductAsync(request.ProductId, ct).ConfigureAwait(false);
            if (response is null || !response.IsSuccess || response.Product is null)
            {
                throw new InvalidOperationException($"Product {request.ProductId} not found in catalog.");
            }
            product = response.Product;
            _cache.Set(cacheKey, product, CacheDuration);
        }

        var baseUnitPrice = product!.UnitPrice;
        var currency = product.Currency;
        var categoryId = product.CategoryId;

        // Step 2: Load active rules
        var rules = await _priceRuleRepo.GetActiveRulesForProductAsync(
            request.ProductId, categoryId, request.Quantity, now, ct).ConfigureAwait(false);

        // Step 3-5: Load promotion code if provided
        PromotionCode? promoCode = null;
        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            promoCode = await _promoCodeRepo.GetByCodeAsync(request.PromoCode.ToUpperInvariant(), ct).ConfigureAwait(false);
            if (promoCode is not null && (!promoCode.CanRedeem(now) || !promoCode.IsApplicableTo(request.ProductId, categoryId)))
            {
                promoCode = null; // Invalid, expired, or not applicable — don't apply
            }
        }

        // Step 6: Run calculation engine (C3 Fix: pass product currency, not hardcoded USD)
        var result = _engine.Calculate(
            request.ProductId,
            request.Quantity,
            baseUnitPrice,
            currency,
            categoryId,
            rules,
            promoCode,
            now);

        // Step 7: Calculate tax
        var taxResult = await _taxCalculator.CalculateAsync(
            request.CountryCode, request.StateCode, result.Subtotal, result.Currency, ct).ConfigureAwait(false);

        // Step 8: Assemble final result with tax
        // M1 Fix: Round final output to 2dp at the boundary (payment gateways expect 2dp).
        // Internal intermediates use 4dp to prevent accumulation errors.
        var subtotal2dp = Math.Round(result.Subtotal, 2, MidpointRounding.AwayFromZero);
        var taxAmount2dp = Math.Round(taxResult.TaxAmount, 2, MidpointRounding.AwayFromZero);
        var total = subtotal2dp + taxAmount2dp;

        var finalResult = result with
        {
            Subtotal = subtotal2dp,
            TaxAmount = taxAmount2dp,
            TaxRate = taxResult.EffectiveRate,
            Total = total,
        };

        // Step 9: Persist audit log
        var appliedRuleIds = JsonSerializer.Serialize(
            result.Discounts.Where(d => !string.Equals(d.Type, "PromotionCode", StringComparison.Ordinal))
                .Select(d => d.Label).ToList());

        var log = PriceCalculationLog.Create(
            productId: request.ProductId,
            quantity: request.Quantity,
            baseUnitPrice: baseUnitPrice,
            effectiveUnitPrice: result.EffectiveUnitPrice,
            subtotal: result.Subtotal,
            taxAmount: taxResult.TaxAmount,
            taxRateApplied: taxResult.EffectiveRate,
            total: total,
            currency: result.Currency,
            appliedRuleIds: appliedRuleIds,
            promotionCodeApplied: promoCode?.Code,
            userId: request.UserId,
            countryCode: request.CountryCode,
            stateCode: request.StateCode,
            snapshotProductPrice: baseUnitPrice);

        await _logRepo.AddAsync(log, ct).ConfigureAwait(false);
        await _logRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Price calculated for product {ProductId}: base={Base}, effective={Effective}, total={Total}",
            request.ProductId, baseUnitPrice, result.EffectiveUnitPrice, total);

        return finalResult with { CalculationId = log.Id };
    }
}
