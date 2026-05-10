using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MassTransit;
using Moq;
using Haworks.Contracts.Catalog;
using Haworks.Pricing.Application.Consumers;
using System.Threading.Tasks;
using System.Text;

namespace Haworks.Pricing.Integration.Consumers;

public class ProductCacheInvalidatedConsumerTests
{
    [Fact]
    public async Task Consume_ProductCacheInvalidated_RemovesFromCache()
    {
        // Arrange
        var opts = Options.Create(new MemoryDistributedCacheOptions());
        var cache = new MemoryDistributedCache(opts);
        var logger = NullLogger<ProductCacheInvalidatedConsumer>.Instance;
        var consumer = new ProductCacheInvalidatedConsumer(cache, logger);

        var productId = System.Guid.NewGuid();
        var key = $"pricing:product:{productId}";
        await cache.SetAsync(key, Encoding.UTF8.GetBytes("old_quote_data"));

        var contextMock = new Mock<ConsumeContext<ProductCacheInvalidatedEvent>>();
        var evt = new ProductCacheInvalidatedEvent 
        { 
            ProductId = productId, 
            Reason = "updated" 
        };
        contextMock.Setup(c => c.Message).Returns(evt);

        // Act
        await consumer.Consume(contextMock.Object);

        // Assert
        var result = await cache.GetAsync(key);
        result.Should().BeNull();
    }
}
