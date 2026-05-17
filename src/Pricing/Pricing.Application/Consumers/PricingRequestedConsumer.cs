using Haworks.Contracts.Pricing;
using Haworks.Pricing.Application.Queries;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Pricing.Application.Consumers;

/// <summary>
/// Handles PricingRequestedEvent from the CheckoutSaga.
/// Calculates price and publishes PriceCalculatedEvent or PricingFailedEvent.
/// </summary>
public sealed class PricingRequestedConsumer : IConsumer<PricingRequestedEvent>
{
    private readonly IMediator _mediator;
    private readonly ILogger<PricingRequestedConsumer> _logger;

    public PricingRequestedConsumer(IMediator mediator, ILogger<PricingRequestedConsumer> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PricingRequestedEvent> context)
    {
        var msg = context.Message;
        _logger.LogInformation(
            "Processing PricingRequestedEvent for order {OrderId}, product {ProductId}",
            msg.OrderId, msg.ProductId);

        try
        {
            var result = await _mediator.Send(new CalculateEffectivePriceQuery
            {
                ProductId = msg.ProductId,
                Quantity = msg.Quantity,
                PromoCode = msg.PromoCode,
                UserId = msg.UserId,
                CountryCode = msg.CountryCode,
                StateCode = msg.StateCode,
            }, context.CancellationToken).ConfigureAwait(false);

            await context.Publish(new PriceCalculatedEvent
            {
                SagaId = msg.SagaId,
                OrderId = msg.OrderId,
                CalculationId = result.CalculationId,
                Subtotal = result.Subtotal,
                TaxAmount = result.TaxAmount,
                Total = result.Total,
                Currency = result.Currency,
            }, context.CancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Price calculated for order {OrderId}: total={Total}", msg.OrderId, result.Total);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pricing failed for order {OrderId}", msg.OrderId);

            await context.Publish(new PricingFailedEvent
            {
                SagaId = msg.SagaId,
                OrderId = msg.OrderId,
                Reason = ex.Message,
            }, context.CancellationToken).ConfigureAwait(false);

            throw;
        }
    }
}
