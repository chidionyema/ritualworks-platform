using Xunit;
using Moq;
using MassTransit;
using FluentAssertions;
using Haworks.Contracts.Orders;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Identity;
using Haworks.Audit.Application.Extraction;

namespace Haworks.Audit.Unit.Extraction;

public class ReflectionAuditExtractorTests
{
    [Fact]
    public void Extract_OrderCreatedEvent_PicksOrderId()
    {
        // Arrange
        var extractor = new ReflectionAuditExtractor<OrderCreatedEvent>();
        var evt = new OrderCreatedEvent 
        { 
            OrderId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            TotalAmountCents = 10000L,
            CustomerEmail = "test@example.com",
            OccurredAt = DateTime.UtcNow
        };
        var contextMock = new Mock<ConsumeContext<OrderCreatedEvent>>();
        contextMock.Setup(c => c.CorrelationId).Returns(Guid.NewGuid());
        contextMock.Setup(c => c.MessageId).Returns(Guid.NewGuid());

        // Act
        var row = extractor.Extract(evt, contextMock.Object);

        // Assert
        row.EntityType.Should().Be("order");
        row.EntityId.Should().Be(evt.OrderId.ToString());
        row.EventType.Should().Be("OrderCreatedEvent");
        row.CorrelationId.Should().Be(contextMock.Object.CorrelationId.ToString());
    }

    [Fact]
    public void Extract_ProductCacheInvalidatedEvent_PicksProductId()
    {
        // Arrange
        var extractor = new ReflectionAuditExtractor<ProductCacheInvalidatedEvent>();
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
        row.EntityType.Should().Be("product");
        row.EntityId.Should().Be(evt.ProductId.ToString());
    }

    [Fact]
    public void Extract_UserProfileChangedEvent_PicksUserId()
    {
        // Arrange
        var extractor = new ReflectionAuditExtractor<UserProfileChangedEvent>();
        var evt = new UserProfileChangedEvent 
        { 
            UserId = Guid.NewGuid().ToString(),
            Email = "user@example.com",
            UserName = "user1",
            Roles = new List<string>(),
            ChangeReason = "Registration",
            OccurredAt = DateTime.UtcNow
        };
        var contextMock = new Mock<ConsumeContext<UserProfileChangedEvent>>();

        // Act
        var row = extractor.Extract(evt, contextMock.Object);

        // Assert
        row.EntityType.Should().Be("user");
        row.EntityId.Should().Be(evt.UserId);
    }

    [Fact]
    public void Extract_EventWithNoKnownId_ReturnsUnknown()
    {
        // Arrange
        var extractor = new ReflectionAuditExtractor<TestEventNoId>();
        var evt = new TestEventNoId { OccurredAt = DateTime.UtcNow };
        var contextMock = new Mock<ConsumeContext<TestEventNoId>>();

        // Act
        var row = extractor.Extract(evt, contextMock.Object);

        // Assert
        row.EntityType.Should().Be("unknown");
        row.EntityId.Should().BeEmpty();
    }

    public record TestEventNoId : Haworks.Contracts.DomainEvent;
}
