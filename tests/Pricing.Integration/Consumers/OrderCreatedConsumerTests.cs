using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MassTransit;
using Moq;
using Haworks.Contracts.Orders;
using Haworks.Pricing.Application.Consumers;
using System.Threading.Tasks;
using System.Text;

namespace Haworks.Pricing.Integration.Consumers;

public class OrderCreatedConsumerTests
{
    [Fact]
    public async Task Consume_OrderCreated_RecordsRedemptionInCache()
    {
        // Arrange
        var opts = Options.Create(new MemoryDistributedCacheOptions());
        var cache = new MemoryDistributedCache(opts);
        var logger = NullLogger<OrderCreatedConsumer>.Instance;
        var consumer = new OrderCreatedConsumer(cache, logger);

        var contextMock = new Mock<ConsumeContext<OrderCreatedEvent>>();
        var evt = new OrderCreatedEvent 
        { 
            OrderId = System.Guid.NewGuid(), 
            CustomerId = System.Guid.NewGuid(), 
            TotalAmount = 10m, 
            CustomerEmail = "test@example.com" 
        };
        contextMock.Setup(c => c.Message).Returns(evt);

        // Act
        await consumer.Consume(contextMock.Object);

        // Assert
        var result = await cache.GetAsync($"pricing:redemption:{evt.OrderId}");
        result.Should().NotBeNull();
        var str = Encoding.UTF8.GetString(result!);
        str.Should().Be("1");
    }
}
