using Haworks.Orders.Domain;

namespace Haworks.Orders.UnitTests.Helpers;

public static class OrderTestHelpers
{
    public static Order CreateOrder(
        string userId = "user-123",
        decimal amount = 99.99m,
        string currency = "USD",
        Guid? sagaId = null,
        string? idempotencyKey = null,
        string email = "test@example.com")
    {
        var items = new[] { (Guid.NewGuid(), "Test Product", 1, amount) };
        return Order.Create(
            userId, amount, currency,
            sagaId ?? Guid.NewGuid(),
            idempotencyKey ?? Guid.NewGuid().ToString(),
            email,
            items);
    }
}
