using System.Net.Http.Json;
using FluentAssertions;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Orders;
using Haworks.Contracts.Payments;
using Haworks.Orders.Domain;
using Haworks.Orders.Infrastructure;

namespace Haworks.Orders.Integration;

[Collection("Orders Integration")]
public sealed class CheckoutSessionExpiredConsumerTests(OrdersWebAppFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly IServiceProvider _services = factory.Services;

    public async Task InitializeAsync()
    {
        await factory.EnsureSchemaAsync();
        var harness = _services.GetRequiredService<ITestHarness>();
        await harness.Start();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Consume_marks_order_Expired_when_pending()
    {
        // Arrange
        var (orderId, _) = await CreateOrderAsync();
        var harness = _services.GetRequiredService<ITestHarness>();

        // Act
        await harness.Bus.Publish(new CheckoutSessionExpiredEvent
        {
            OrderId = orderId,
            PaymentId = Guid.NewGuid(),
            SessionId = "sess_123",
            Provider = "Stripe"
        });

        // Assert
        await PollUntilAsync(() => harness.Published.Select<StockReleaseRequestedEvent>().Any(p => p.Context.Message.OrderId == orderId),
            TimeSpan.FromSeconds(30));

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var stored = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        stored.Status.Should().Be(OrderStatus.Expired);
        stored.AbandonReason.Should().Be("checkout_session_expired");
    }

    [Fact]
    public async Task Consume_is_idempotent_for_terminal_order()
    {
        // Arrange
        var (orderId, _) = await CreateOrderAsync();
        var harness = _services.GetRequiredService<ITestHarness>();

        // Move to Paid (terminal)
        await harness.Bus.Publish(new PaymentCompletedEvent
        {
            PaymentId = Guid.NewGuid(),
            OrderId = orderId,
            SagaId = Guid.NewGuid(),
            Amount = 25.50m,
            Currency = "USD",
            Provider = "Stripe",
            TransactionReference = "pi_xyz",
        });
        await PollUntilAsync(() => harness.Published.Select<OrderCompletedEvent>().Any(p => p.Context.Message.OrderId == orderId),
            TimeSpan.FromSeconds(30));

        // Act
        await harness.Bus.Publish(new CheckoutSessionExpiredEvent
        {
            OrderId = orderId,
            PaymentId = Guid.NewGuid(),
            SessionId = "sess_123",
            Provider = "Stripe"
        });

        // Give it a moment to process
        await Task.Delay(500);

        // Assert - should still be Paid
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var stored = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        stored.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Consume_is_noop_for_unknown_order()
    {
        // Arrange
        var harness = _services.GetRequiredService<ITestHarness>();
        var randomOrderId = Guid.NewGuid();

        // Act
        await harness.Bus.Publish(new CheckoutSessionExpiredEvent
        {
            OrderId = randomOrderId,
            PaymentId = Guid.NewGuid(),
            SessionId = "sess_unknown",
            Provider = "Stripe"
        });

        // Assert - should not fault and should be consumed
        await PollUntilAsync(() => harness.Consumed.Select<CheckoutSessionExpiredEvent>().Any(m => m.Context.Message.OrderId == randomOrderId),
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Consume_publishes_stock_release_event()
    {
        // Arrange
        var (orderId, _) = await CreateOrderAsync();
        var harness = _services.GetRequiredService<ITestHarness>();

        // Act
        await harness.Bus.Publish(new CheckoutSessionExpiredEvent
        {
            OrderId = orderId,
            PaymentId = Guid.NewGuid(),
            SessionId = "sess_456",
            Provider = "Stripe"
        });

        // Assert
        await PollUntilAsync(() => harness.Published.Select<StockReleaseRequestedEvent>().Any(p => p.Context.Message.OrderId == orderId),
            TimeSpan.FromSeconds(30));

        var releaseEvent = harness.Published.Select<StockReleaseRequestedEvent>()
            .First(p => p.Context.Message.OrderId == orderId);
        
        releaseEvent.Context.Message.Reason.Should().Be("checkout_session_expired");
        releaseEvent.Context.Message.Items.Should().NotBeEmpty();
        releaseEvent.Context.Message.Items[0].ProductName.Should().Be("Widget");
    }

    private async Task<(Guid orderId, string userId)> CreateOrderAsync()
    {
        var userId = Guid.NewGuid().ToString();
        var resp = await _client.PostAsJsonAsync("/api/orders", new
        {
            userId,
            customerEmail = "buyer@example.com",
            totalAmount = 25.50m,
            currency = "USD",
            sagaId = Guid.NewGuid(),
            idempotencyKey = "key-" + Guid.NewGuid().ToString("N"),
            items = new[]
            {
                new { productId = Guid.NewGuid(), productName = "Widget", quantity = 1, unitPrice = 25.50m },
            }
        });
        resp.EnsureSuccessStatusCode();
        var orderId = await resp.Content.ReadFromJsonAsync<Guid>();
        return (orderId, userId);
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
