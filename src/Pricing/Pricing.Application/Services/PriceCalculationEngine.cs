using System.Text.Json;
using Haworks.Pricing.Domain.Entities;
using Haworks.Pricing.Domain.Enums;
using Haworks.Pricing.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Haworks.Pricing.Application.Services;

/// <summary>
/// Pure calculation engine. No database, no HTTP — just math.
/// Registered as Singleton (no scoped dependencies).
/// </summary>
public sealed class PriceCalculationEngine
{
    private readonly ILogger<PriceCalculationEngine> _logger;

    public PriceCalculationEngine(ILogger<PriceCalculationEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculates effective price for a product given rules, tiers, and optional promotion.
    /// Returns a PriceBreakdownResult with all discount details.
    /// </summary>
    public PriceBreakdownResult Calculate(
        Guid productId,
        int quantity,
        decimal baseUnitPrice,
        Guid? categoryId,
        IReadOnlyList<PriceRule> rules,
        PromotionCode? promotionCode,
        DateTimeOffset now)
    {
        var discounts = new List<AppliedDiscount>();
        var appliedRuleIds = new List<Guid>();
        var effectiveUnitPrice = baseUnitPrice;

        // 1. Sort rules by Priority DESC, then specificity (ProductId > CategoryId)
        var applicableRules = rules
            .Where(r => r.IsApplicableTo(productId, categoryId, quantity, now))
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.ProductId.HasValue ? 2 : r.CategoryId.HasValue ? 1 : 0)
            .ToList();

        // 2. Check TieredPrices first
        foreach (var rule in applicableRules)
        {
            var matchingTier = rule.TieredPrices
                .FirstOrDefault(t => t.ContainsQuantity(quantity));

            if (matchingTier is not null)
            {
                var tierDiscount = effectiveUnitPrice - matchingTier.UnitPrice;
                if (tierDiscount > 0)
                {
                    var pct = baseUnitPrice > 0
                        ? Math.Round(tierDiscount / baseUnitPrice * 100m, 2, MidpointRounding.AwayFromZero)
                        : 0;

                    discounts.Add(new AppliedDiscount
                    {
                        Type = "TieredVolume",
                        Label = $"Buy {matchingTier.FromQuantity}+ get tiered price",
                        AmountOff = Math.Round(tierDiscount, 4, MidpointRounding.AwayFromZero),
                        Pct = pct,
                    });
                    appliedRuleIds.Add(rule.Id);
                }
                effectiveUnitPrice = matchingTier.UnitPrice;
                break; // Only one tier applies
            }
        }

        // 3. Apply remaining rules in priority order
        foreach (var rule in applicableRules)
        {
            if (appliedRuleIds.Contains(rule.Id)) continue; // Already applied via tier

            switch (rule.DiscountType)
            {
                case DiscountType.Percentage:
                    var pctOff = Math.Round(effectiveUnitPrice * rule.DiscountValue / 100m, 4, MidpointRounding.AwayFromZero);
                    effectiveUnitPrice -= pctOff;
                    discounts.Add(new AppliedDiscount
                    {
                        Type = "Percentage",
                        Label = $"{rule.DiscountValue}% off",
                        AmountOff = Math.Round(pctOff, 4, MidpointRounding.AwayFromZero),
                        Pct = rule.DiscountValue,
                    });
                    appliedRuleIds.Add(rule.Id);
                    break;

                case DiscountType.FixedAmount:
                    var fixedOff = Math.Min(rule.DiscountValue, effectiveUnitPrice);
                    effectiveUnitPrice -= fixedOff;
                    discounts.Add(new AppliedDiscount
                    {
                        Type = "FixedAmount",
                        Label = $"${rule.DiscountValue} off",
                        AmountOff = Math.Round(fixedOff, 4, MidpointRounding.AwayFromZero),
                    });
                    appliedRuleIds.Add(rule.Id);
                    break;

                case DiscountType.FreeShipping:
                    // Not applied to price calculation in v1
                    _logger.LogDebug("FreeShipping rule {RuleId} skipped in v1", rule.Id);
                    break;

                default:
                    _logger.LogWarning("Unknown DiscountType {Type} on rule {RuleId}, skipping", rule.DiscountType, rule.Id);
                    break;
            }

            // Floor at zero
            effectiveUnitPrice = Math.Max(0, effectiveUnitPrice);
        }

        effectiveUnitPrice = Math.Round(effectiveUnitPrice, 4, MidpointRounding.AwayFromZero);

        // 4. Calculate subtotal
        var subtotal = Math.Round(effectiveUnitPrice * quantity, 4, MidpointRounding.AwayFromZero);

        // 5. Apply promotion code to subtotal (not per-unit)
        if (promotionCode is not null && promotionCode.CanRedeem(now))
        {
            switch (promotionCode.DiscountType)
            {
                case DiscountType.Percentage:
                    var promoOff = Math.Round(subtotal * promotionCode.DiscountValue / 100m, 4, MidpointRounding.AwayFromZero);
                    subtotal -= promoOff;
                    discounts.Add(new AppliedDiscount
                    {
                        Type = "PromotionCode",
                        Label = promotionCode.Code,
                        AmountOff = Math.Round(promoOff, 4, MidpointRounding.AwayFromZero),
                        Pct = promotionCode.DiscountValue,
                    });
                    break;

                case DiscountType.FixedAmount:
                    var promoFixedOff = Math.Min(promotionCode.DiscountValue, subtotal);
                    subtotal -= promoFixedOff;
                    discounts.Add(new AppliedDiscount
                    {
                        Type = "PromotionCode",
                        Label = promotionCode.Code,
                        AmountOff = Math.Round(promoFixedOff, 4, MidpointRounding.AwayFromZero),
                    });
                    break;
            }

            subtotal = Math.Max(0, subtotal);
        }

        subtotal = Math.Round(subtotal, 4, MidpointRounding.AwayFromZero);

        return new PriceBreakdownResult
        {
            CalculationId = Guid.NewGuid(),
            ProductId = productId,
            Quantity = quantity,
            Currency = "USD",
            BaseUnitPrice = baseUnitPrice,
            EffectiveUnitPrice = effectiveUnitPrice,
            Discounts = discounts,
            Subtotal = subtotal,
            TaxAmount = 0m, // Filled in by caller after tax calculation
            TaxRate = 0m,   // Filled in by caller
            Total = subtotal, // Updated after tax
            PromoCodeApplied = promotionCode?.Code,
            SnapshotAt = now,
        };
    }
}
