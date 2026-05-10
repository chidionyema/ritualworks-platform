using FluentAssertions;
using Haworks.Orders.Domain;
using Xunit;

namespace Haworks.Orders.Unit.Domain;

public class OrderTests
{
    private static readonly List<(Guid productId, string productName, int quantity, decimal unitPrice)> DefaultItems = 
        new() { (Guid.NewGuid(), "Product 1", 1, 100.00m) };

    #region Factory Method Tests

    [Fact]
    public void Create_WithValidParameters_CreatesOrderWithCorrectValues()
    {
        // Arrange
        var userId = "user-123";
        var totalAmount = 100.00m;
        var currency = "USD";
        var sagaId = Guid.NewGuid();
        var idempotencyKey = "key-123";
        var customerEmail = "test@example.com";

        // Act
        var order = Order.Create(userId, totalAmount, currency, sagaId, idempotencyKey, customerEmail, DefaultItems);

        // Assert
        order.Id.Should().NotBeEmpty();
        order.UserId.Should().Be(userId);
        order.TotalAmount.Should().Be(totalAmount);
        order.Currency.Should().Be(currency);
        order.SagaId.Should().Be(sagaId);
        order.IdempotencyKey.Should().Be(idempotencyKey);
        order.CustomerEmail.Should().Be(customerEmail);
        order.Status.Should().Be(OrderStatus.Created);
        order.Items.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidUserId_ThrowsArgumentException(string? userId)
    {
        Assert.ThrowsAny<ArgumentException>(() => 
            Order.Create(userId!, 100m, "USD", Guid.NewGuid(), "key", "email", DefaultItems));
    }

    [Fact]
    public void Create_WithNegativeAmount_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => 
            Order.Create("user", -1m, "USD", Guid.NewGuid(), "key", "email", DefaultItems));
    }

    [Fact]
    public void Create_WithEmptySagaId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => 
            Order.Create("user", 100m, "USD", Guid.Empty, "key", "email", DefaultItems));
    }

    [Fact]
    public void Create_WithEmptyItems_ThrowsArgumentException()
    {
        var emptyItems = new List<(Guid, string, int, decimal)>();
        Assert.Throws<ArgumentException>(() => 
            Order.Create("user", 100m, "USD", Guid.NewGuid(), "key", "email", emptyItems));
    }

    #endregion

    #region Status Transition Tests

    [Fact]
    public void MarkPaid_FromCreated_SetsStatusAndPaymentId()
    {
        // Arrange
        var order = Order.Create("user", 100m, "USD", Guid.NewGuid(), "key", "email", DefaultItems);
        var paymentId = Guid.NewGuid();

        // Act
        var result = order.MarkPaid(paymentId);

        // Assert
        result.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        order.PaymentId.Should().Be(paymentId);
    }

    [Fact]
    public void MarkPaid_AlreadyPaid_ReturnsFalse()
    {
        // Arrange
        var order = Order.Create("user", 100m, "USD", Guid.NewGuid(), "key", "email", DefaultItems);
        order.MarkPaid(Guid.NewGuid());

        // Act
        var result = order.MarkPaid(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public void MarkPaid_AlreadyAbandoned_ReturnsFalse()
    {
        // Arrange
        var order = Order.Create("user", 100m, "USD", Guid.NewGuid(), "key", "email", DefaultItems);
        order.MarkAbandoned("failure");

        // Act
        var result = order.MarkPaid(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Abandoned);
    }

    [Fact]
    public void MarkAbandoned_FromCreated_SetsStatusAndReason()
    {
        // Arrange
        var order = Order.Create("user", 100m, "USD", Guid.NewGuid(), "key", "email", DefaultItems);
        var reason = "Stock timeout";

        // Act
        var result = order.MarkAbandoned(reason);

        // Assert
        result.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Abandoned);
        order.AbandonReason.Should().Be(reason);
    }

    [Fact]
    public void MarkAbandoned_AlreadyPaid_ReturnsFalse()
    {
        // Arrange
        var order = Order.Create("user", 100m, "USD", Guid.NewGuid(), "key", "email", DefaultItems);
        order.MarkPaid(Guid.NewGuid());

        // Act
        var result = order.MarkAbandoned("timeout");

        // Assert
        result.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Paid);
    }

    #endregion

    #region OrderItem Verification

    [Fact]
    public void Create_ItemsAreCorrectlyPopulated()
    {
        // Arrange
        var items = new List<(Guid productId, string productName, int quantity, decimal unitPrice)>
        {
            (Guid.NewGuid(), "Product A", 2, 50.00m),
            (Guid.NewGuid(), "Product B", 1, 30.00m)
        };

        // Act
        var order = Order.Create("user", 130m, "USD", Guid.NewGuid(), "key", "email", items);

        // Assert
        order.Items.Should().HaveCount(2);
        order.Items.Should().Contain(i => i.ProductName == "Product A" && i.Quantity == 2 && i.UnitPrice == 50.00m);
        order.Items.Should().Contain(i => i.ProductName == "Product B" && i.Quantity == 1 && i.UnitPrice == 30.00m);
    }

    #endregion
}
