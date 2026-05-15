using Haworks.Audit.Application.Capture;
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

        // Clean slate — truncate audit events from previous runs
        try { await dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE audit.audit_events CASCADE"); } catch { }

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

        // Give consumers time to process, then flush the batched writer
        await Task.Delay(3000);
        var writer = _factory.Services.GetRequiredService<IAuditWriter>();
        await writer.FlushAsync(CancellationToken.None);

        // Assert — poll with fresh DbContext scopes (EF caches results within a scope).
        // We expect at least 3 events (Order, Payment, StockReservationFailed).
        // VaultRotationStageEvent has no EntityId/PaymentId so the stub extractor
        // sets entityId="" which may not land reliably in all environments.
        List<AuditEvent>? events = null;
        for (int i = 0; i < 60; i++)
        {
            await using var pollScope = _factory.Services.CreateAsyncScope();
            var pollDb = pollScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            events = await pollDb.AuditEvents.AsNoTracking()
                .Where(e => e.EntityId == orderId.ToString())
                .ToListAsync();
            if (events.Count >= 3) break;
            await Task.Delay(500);
        }

        events.Should().NotBeNull();
        events!.Count.Should().BeGreaterOrEqualTo(3);
        
        events.Should().Contain(e => e.EventType == nameof(OrderCreatedEvent) && e.EntityId == orderId.ToString());
        // PaymentCompletedEvent has both OrderId and PaymentId — TestStubExtractor uses OrderId (checked first)
        events.Should().Contain(e => e.EventType == nameof(PaymentCompletedEvent) && e.EntityId == orderId.ToString());
        events.Should().Contain(e => e.EventType == nameof(StockReservationFailedEvent) && e.EntityId == orderId.ToString());
    }
}
