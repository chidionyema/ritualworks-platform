using MassTransit;
using Microsoft.Extensions.Logging;
using Haworks.Contracts.Orders;
using Microsoft.Extensions.Caching.Distributed;
using System.Threading.Tasks;

namespace Haworks.Pricing.Application.Consumers;

public sealed class OrderCreatedConsumer(
    IDistributedCache cache,
    ILogger<OrderCreatedConsumer> logger
) : IConsumer<OrderCreatedEvent>
{
    public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
    {
        var evt = context.Message;
        logger.LogInformation("Recording redemption for order {OrderId}", evt.OrderId);
        
        var key = $"pricing:redemption:{evt.OrderId}";
        await cache.SetStringAsync(key, "1", context.CancellationToken);
    }
}
