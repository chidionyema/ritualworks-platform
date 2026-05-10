using FluentAssertions;
using Haworks.Audit.Domain;using Haworks.Audit.Infrastructure.Persistence;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Identity;
using Haworks.Contracts.Orders;
using Haworks.Contracts.Payments;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.Audit.Integration;

public sealed class EndToEndCaptureTests : IClassFixture<AuditWebAppFactory>
{
    private readonly AuditWebAppFactory _factory;

    public EndToEndCaptureTests(AuditWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InboundEvents_ShouldBeCapturedInDatabase()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        // 1. OrderCreatedEvent
        await bus.Publish(new OrderCreatedEvent
        {
            OrderId = orderId,
            CustomerId = Guid.NewGuid(),
            TotalAmount = 99.99m,
            CustomerEmail = "test@example.com"
        });

        // 2. PaymentCompletedEvent
        await bus.Publish(new PaymentCompletedEvent
        {
            PaymentId = paymentId,
            OrderId = orderId,
            SagaId = Guid.NewGuid(),
            Amount = 99.99m,
            Currency = "USD",
            Provider = "Stripe",
            TransactionReference = "tx_123"
        });

        // 3. StockReservationFailedEvent
        await bus.Publish(new StockReservationFailedEvent
        {
            OrderId = orderId,
            SagaId = Guid.NewGuid(),
            Reason = "Out of stock",
            FailedItems = new List<FailedReservationItem>
            {
                new() { ProductId = Guid.NewGuid(), ProductName = "Test", RequestedQuantity = 1 }
            }
        });

        // 4. VaultRotationStageEvent
        await bus.Publish(new VaultRotationStageEvent
        {
            SessionId = Guid.NewGuid(),
            Stage = "applied",
            NewVersion = 2,
            PreviousVersion = "1",
            Timestamp = DateTime.UtcNow
        });

        // Assert
        // Poll for up to 10 seconds for events to land in DB (asynchronous processing)
        List<AuditEvent>? events = null;
        for (int i = 0; i < 20; i++)
        {
            events = await dbContext.AuditEvents.AsNoTracking().ToListAsync();
            if (events.Count >= 4) break;
            await Task.Delay(500);
        }

        events.Should().NotBeNull();
        events!.Count.Should().BeGreaterOrEqualTo(4);
        
        events.Should().Contain(e => e.EventType == nameof(OrderCreatedEvent) && e.EntityId == orderId.ToString());
        events.Should().Contain(e => e.EventType == nameof(PaymentCompletedEvent) && e.EntityId == paymentId.ToString());
        events.Should().Contain(e => e.EventType == nameof(StockReservationFailedEvent) && e.EntityId == orderId.ToString());
        events.Should().Contain(e => e.EventType == nameof(VaultRotationStageEvent));
    }
}
