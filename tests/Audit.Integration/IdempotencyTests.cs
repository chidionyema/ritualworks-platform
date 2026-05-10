using FluentAssertions;
using Haworks.Audit.Infrastructure.Persistence;
using Haworks.Contracts.Orders;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.Audit.Integration;

public sealed class IdempotencyTests : IClassFixture<AuditWebAppFactory>
{
    private readonly AuditWebAppFactory _factory;

    public IdempotencyTests(AuditWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SameMessageId_ShouldBeCapturedOnlyOnce()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var messageId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // Act
        // Publish the same event twice with the same MessageId
        await bus.Publish(new OrderCreatedEvent
        {
            OrderId = orderId,
            CustomerId = Guid.NewGuid(),
            TotalAmount = 50.00m,
            CustomerEmail = "idempotent@example.com"
        }, context => context.MessageId = messageId);

        await bus.Publish(new OrderCreatedEvent
        {
            OrderId = orderId,
            CustomerId = Guid.NewGuid(),
            TotalAmount = 50.00m,
            CustomerEmail = "idempotent@example.com"
        }, context => context.MessageId = messageId);

        // Assert
        // Poll for up to 5 seconds
        for (int i = 0; i < 10; i++)
        {
            var count = await dbContext.AuditEvents.CountAsync(e => e.EventType == nameof(OrderCreatedEvent) && e.EntityId == orderId.ToString());
            if (count > 0) break;
            await Task.Delay(500);
        }

        // Wait a bit more to see if a second one arrives (it shouldn't)
        await Task.Delay(2000);

        var finalEvents = await dbContext.AuditEvents
            .Where(e => e.EventType == nameof(OrderCreatedEvent) && e.EntityId == orderId.ToString())
            .ToListAsync();

        finalEvents.Should().HaveCount(1, "because the second message with the same MessageId should be ignored by the unique index");
    }
}
