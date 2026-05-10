using Xunit;
using Moq;
using MassTransit;
using FluentAssertions;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Identity;
using Haworks.Audit.Application.Extraction;

namespace Haworks.Audit.Unit.Extraction;

public class OverrideTests
{
    [Fact]
    public void StockReservationFailedExtractor_PicksOrderId()
    {
        // Arrange
        var extractor = new StockReservationFailedExtractor();
        var evt = new StockReservationFailedEvent 
        { 
            OrderId = Guid.NewGuid(),
            Reason = "No stock",
            SagaId = Guid.NewGuid(),
            FailedItems = new List<FailedReservationItem>(),
            OccurredAt = DateTime.UtcNow
        };
        var contextMock = new Mock<ConsumeContext<StockReservationFailedEvent>>();

        // Act
        var row = extractor.Extract(evt, contextMock.Object);

        // Assert
        row.EntityType.Should().Be("order");
        row.EntityId.Should().Be(evt.OrderId.ToString());
    }

    [Fact]
    public void VaultRotationStageExtractor_ReturnsSystemType()
    {
        // Arrange
        var extractor = new VaultRotationStageExtractor();
        var evt = new VaultRotationStageEvent 
        { 
            SessionId = Guid.NewGuid(),
            Stage = "started",
            NewVersion = 2,
            PreviousVersion = "1",
            Timestamp = DateTime.UtcNow,
            OccurredAt = DateTime.UtcNow
        };
        var contextMock = new Mock<ConsumeContext<VaultRotationStageEvent>>();

        // Act
        var row = extractor.Extract(evt, contextMock.Object);

        // Assert
        row.EntityType.Should().Be("system");
        row.EntityId.Should().Be("identity-svc");
        row.ActorType.Should().Be("system");
    }

    [Fact]
    public void ProductCacheInvalidatedExtractor_ReturnsCacheType()
    {
        // Arrange
        var extractor = new ProductCacheInvalidatedExtractor();
        var evt = new ProductCacheInvalidatedEvent 
        { 
            ProductId = Guid.NewGuid(),
            Reason = "updated",
            OccurredAt = DateTime.UtcNow
        };
        var contextMock = new Mock<ConsumeContext<ProductCacheInvalidatedEvent>>();

        // Act
        var row = extractor.Extract(evt, contextMock.Object);

        // Assert
        row.EntityType.Should().Be("cache");
        row.EntityId.Should().Be(evt.ProductId.ToString());
    }
}
