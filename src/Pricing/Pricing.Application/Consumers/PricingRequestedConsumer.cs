using System.Linq;
using Haworks.Contracts.Pricing;
using Haworks.Pricing.Application.Commands;
using Haworks.Pricing.Application.Queries;
using Haworks.Pricing.Domain.Exceptions;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Pricing.Application.Consumers;

/// <summary>
/// Handles PricingRequestedEvent from the CheckoutSaga.
/// Calculates price and publishes PriceCalculatedEvent or PricingFailedEvent.
/// </summary>
public sealed class PricingRequestedConsumer(
    IMediator mediator,
    ILogger<PricingRequestedConsumer> logger) : IConsumer<PricingRequestedEvent>
{
    public async Task Consume(ConsumeContext<PricingRequestedEvent> context)
    {
        var msg = context.Message;

        logger.LogInformation(
            "Processing PricingRequestedEvent for order {OrderId}, product {ProductId}",
            msg.OrderId, msg.ProductId);

        try
        {
            var result = await mediator.Send(new CalculateEffectivePriceQuery
            {
                ProductId = msg.ProductId,
                Quantity = msg.Quantity,
                PromoCode = msg.PromoCode,
                UserId = msg.UserId,
                CountryCode = msg.CountryCode,
                StateCode = msg.StateCode,
            }, context.CancellationToken);

            // C1 Fix: Redeem promo code BEFORE publishing (atomic with the calculation).
            // Without this, the saga uses the discounted price but the code's use-count
            // is never decremented — allowing unlimited reuse.
            if (!string.IsNullOrWhiteSpace(msg.PromoCode) && result.Discounts.Any(d =>
                string.Equals(d.Type, "PromotionCode", System.StringComparison.Ordinal)))
            {
                var promoDiscount = result.Discounts
                    .First(d => string.Equals(d.Type, "PromotionCode", System.StringComparison.Ordinal));

                await mediator.Send(new Commands.RedeemPromotionCodeCommand
                {
                    Code = msg.PromoCode!.ToUpperInvariant(),
                    OrderId = msg.OrderId,
                    UserId = msg.UserId,
                    DiscountAmount = promoDiscount.AmountOff,
                    CalculationId = result.CalculationId,
                }, context.CancellationToken);
            }

            await context.Publish(new PriceCalculatedEvent
            {
                SagaId = msg.SagaId,
                OrderId = msg.OrderId,
                CalculationId = result.CalculationId,
                SubtotalCents = (long)Math.Round(result.Subtotal * 100m, 0),
                TaxCents = (long)Math.Round(result.TaxAmount * 100m, 0),
                TotalCents = (long)Math.Round(result.Total * 100m, 0),
                Currency = result.Currency,
            }, context.CancellationToken);

            logger.LogInformation(
                "Price calculated for order {OrderId}: total={Total}", msg.OrderId, result.Total);
        }
        // H5 Fix: InvalidOperationException = product not found (catalog failure) — business fault, not infra
        catch (Exception ex) when (ex is PromotionExhaustedException or TaxCalculationException or InvalidOperationException or Haworks.BuildingBlocks.Common.ValidationException)
        {
            logger.LogWarning(ex, "Pricing business rule failed for order {OrderId}: {Reason}", msg.OrderId, ex.Message);

            await context.Publish(new PricingFailedEvent
            {
                SagaId = msg.SagaId,
                OrderId = msg.OrderId,
                Reason = ex.Message,
            }, context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected infrastructure failure during pricing for order {OrderId}", msg.OrderId);
            throw;
        }
    }
}
