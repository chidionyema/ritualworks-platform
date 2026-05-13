using System.Text.Json;
using Confluent.Kafka;
using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Application.Interfaces;
using Haworks.Webhooks.Infrastructure.Workers;
using Haworks.Webhooks.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace Haworks.Webhooks.Unit.Infrastructure;

public class CdcFanOutWorkerTests
{
    private readonly Mock<IConsumer<string, string>> _mockConsumer = new();
    private readonly Mock<IBackgroundJobClient> _mockJobClient = new();
    private readonly Mock<ILogger<CdcFanOutWorker>> _mockLogger = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly WebhooksDbContext _db;

    public CdcFanOutWorkerTests()
    {
        var options = new DbContextOptionsBuilder<WebhooksDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new WebhooksDbContext(options);

        var services = new ServiceCollection();
        services.AddSingleton<IWebhooksDbContext>(_db);
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Worker_Should_Enqueue_Webhook_On_CDC_Message()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var messageValue = JsonSerializer.Serialize(new
        {
            op = "c",
            after = new
            {
                id = productId.ToString(),
                name = "Test Product"
            },
            ts_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var consumeResult = new ConsumeResult<string, string>
        {
            Topic = "db.catalog.public.products",
            Message = new Message<string, string> { Value = messageValue, Key = productId.ToString() }
        };

        _mockConsumer.SetupSequence(c => c.Consume(It.IsAny<CancellationToken>()))
            .Returns(consumeResult)
            .Throws(new OperationCanceledException());

        // Setup subscription in In-Memory DB
        var sub = new WebhookSubscription(Guid.NewGuid(), "https://test.com", "s", "sh", "p", ["products.created"]);
        _db.Subscriptions.Add(sub);
        await _db.SaveChangesAsync();

        var worker = new CdcFanOutWorker(_mockConsumer.Object, _serviceProvider, _mockJobClient.Object, _mockLogger.Object);

        // Act
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(cts.Token);

        // Assert
        _mockJobClient.Verify(x => x.Create(
            It.Is<Job>(j => j.Method.Name == "DispatchAsync" && (Guid)j.Args[0] != Guid.Empty),
            It.IsAny<IState>()), 
            Times.Once);

        var delivery = await _db.Deliveries.FirstOrDefaultAsync();
        Assert.NotNull(delivery);
        Assert.Equal(sub.Id, delivery.SubscriptionId);
        Assert.Equal("products.created", delivery.EventType);
    }
}
