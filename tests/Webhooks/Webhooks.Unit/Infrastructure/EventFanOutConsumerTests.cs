using Haworks.Contracts.Orders;
using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Application.Interfaces;
using Haworks.Webhooks.Infrastructure.Messaging;
using Haworks.Webhooks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MassTransit;

namespace Haworks.Webhooks.Unit.Infrastructure;

public class EventFanOutConsumerTests
{
    private readonly WebhooksDbContext _db;
    private readonly Mock<IBackgroundJobClient> _mockJobClient = new();
    private readonly Mock<ILogger<EventFanOutConsumer>> _mockLogger = new();

    public EventFanOutConsumerTests()
    {
        var options = new DbContextOptionsBuilder<WebhooksDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new WebhooksDbContext(options);
    }

    [Fact]
    public async Task Consumer_Should_Enqueue_Webhook_On_Domain_Event()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var message = new OrderCreatedEvent 
        { 
            OrderId = orderId, 
            CustomerId = Guid.NewGuid(), 
            TotalAmount = 100,
            CustomerEmail = "test@example.com"
        };
        
        var mockContext = new Mock<ConsumeContext<OrderCreatedEvent>>();
        mockContext.Setup(x => x.Message).Returns(message);
        mockContext.Setup(x => x.MessageId).Returns(Guid.NewGuid());

        // Setup subscription
        var sub = new WebhookSubscription(Guid.NewGuid(), "https://test.com", "s", "sh", "p", ["order.created"]);
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var consumer = new EventFanOutConsumer(_db, _mockJobClient.Object, _mockLogger.Object);

        // Act
        await consumer.Consume(mockContext.Object);

        // Simulate MassTransit EF Outbox auto-commit (no outbox pipeline in unit tests)
        await _db.SaveChangesAsync();

        // Assert
        _mockJobClient.Verify(x => x.Create(
            It.Is<Job>(j => j.Method.Name == "DispatchAsync"),
            It.IsAny<IState>()),
            Times.Once);

        var delivery = await _db.Deliveries.FirstOrDefaultAsync();
        Assert.NotNull(delivery);
        Assert.Equal("order.created", delivery.EventType);
        Assert.Contains(orderId.ToString(), delivery.Payload, StringComparison.Ordinal);
    }
}
