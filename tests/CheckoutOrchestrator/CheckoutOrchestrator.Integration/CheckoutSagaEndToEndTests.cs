using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Checkout;
using Haworks.Contracts.Payments;
using Haworks.CheckoutOrchestrator.Application.Sagas;
using Haworks.CheckoutOrchestrator.Domain;
using Haworks.CheckoutOrchestrator.Infrastructure;

namespace Haworks.CheckoutOrchestrator.Integration;

public sealed class CheckoutSagaEndToEndTests : IClassFixture<CheckoutWebAppFactory>, IAsyncLifetime
{
    private readonly CheckoutWebAppFactory _factory;

    public CheckoutSagaEndToEndTests(CheckoutWebAppFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureSchemaAsync();
        await _factory.ResetDatabaseAsync();
        var harness = _factory.Services.GetRequiredService<ITestHarness>();
        harness.TestTimeout = TimeSpan.FromSeconds(30);
        harness.TestInactivityTimeout = TimeSpan.FromSeconds(10);
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task HappyPath_ReachesStockHeld_WhenStockReservedArrives()
    {
        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // 1. Initiate checkout
        await PublishAsync(new CheckoutInitiatedEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
            UserId = "test-user",
            CustomerEmail = "test@example.com",
            TotalAmount = 100m,
            Items = new[] { new CheckoutItemData { ProductId = Guid.NewGuid(), ProductName = "Test", Quantity = 1, UnitPrice = 100m } },
            IdempotencyKey = "key-" + Guid.NewGuid()
        });

        await PollUntilAsync(() => string.Equals(SagaStateOrNull(sagaId), "Initiated", StringComparison.Ordinal), TimeSpan.FromSeconds(15));

        // 2. Simulate StockReserved from Catalog context
        await PublishAsync(new StockReservedEvent
        {
            OrderId = orderId,
            SagaId = sagaId,
            UserId = "test-user",
            TotalAmount = 100m,
            Currency = "USD",
            CustomerEmail = "test@example.com",
            Items = new[] { new StockReservationItem { ProductId = Guid.NewGuid(), ProductName = "Test", Quantity = 1, RemainingStock = 5 } },
            OrderLineItems = new[] { new CheckoutItemData { ProductId = Guid.NewGuid(), ProductName = "Test", Quantity = 1, UnitPrice = 100m } }
        });

        // 3. Assert state transition to StockReservedState (which represents "StockHeld" in monolith)
        await PollUntilAsync(() => string.Equals(SagaStateOrNull(sagaId), "StockReservedState", StringComparison.Ordinal), TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState!.CurrentState.Should().Be("StockReservedState");
        sagaState.ReservedItemsJson.Should().NotBeNull();
    }

    [Fact]
    public async Task FailurePath_AbandonsSaga_WhenStockReservationFails()
    {
        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await PublishAsync(new CheckoutInitiatedEvent
        {
            SagaId = sagaId,
            OrderId = orderId,
            UserId = "test-user",
            CustomerEmail = "test@example.com",
            TotalAmount = 100m,
            Items = new[] { new CheckoutItemData { ProductId = Guid.NewGuid(), ProductName = "Test", Quantity = 1, UnitPrice = 100m } },
            IdempotencyKey = "key-" + Guid.NewGuid()
        });

        await PollUntilAsync(() => string.Equals(SagaStateOrNull(sagaId), "Initiated", StringComparison.Ordinal), TimeSpan.FromSeconds(15));

        // Simulate StockReservationFailed
        await PublishAsync(new StockReservationFailedEvent
        {
            OrderId = orderId,
            SagaId = sagaId,
            Reason = "Insufficient stock",
            FailedItems = new[] { new FailedReservationItem { ProductId = Guid.NewGuid(), ProductName = "Test", RequestedQuantity = 1, AvailableQuantity = 0 } }
        });

        await PollUntilAsync(() => string.Equals(SagaStateOrNull(sagaId), "Abandoned", StringComparison.Ordinal), TimeSpan.FromSeconds(15));

        var sagaState = await ReadSagaAsync(sagaId);
        sagaState!.CurrentState.Should().Be("Abandoned");
    }

    private async Task PublishAsync<T>(T evt) where T : class
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        await publisher.Publish(evt);
    }

    private string? SagaStateOrNull(Guid sagaId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        return db.CheckoutSagas.AsNoTracking()
            .Where(s => s.CorrelationId == sagaId)
            .Select(s => s.CurrentState)
            .FirstOrDefault();
    }

    private async Task<CheckoutSagaState?> ReadSagaAsync(Guid sagaId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        return await db.CheckoutSagas.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CorrelationId == sagaId);
    }

    private static async Task PollUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(250);
        }
    }
}
