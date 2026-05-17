using Haworks.Contracts.Pricing;
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

            await context.Publish(new PriceCalculatedEvent
            {
                SagaId = msg.SagaId,
                OrderId = msg.OrderId,
                CalculationId = result.CalculationId,
                Subtotal = result.Subtotal,
                TaxAmount = result.TaxAmount,
                Total = result.Total,
                Currency = result.Currency,
            }, context.CancellationToken);

            logger.LogInformation(
                "Price calculated for order {OrderId}: total={Total}", msg.OrderId, result.Total);
        }
        catch (Exception ex) when (ex is PromotionExhaustedException or TaxCalculationException or Haworks.BuildingBlocks.Common.ValidationException)
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
