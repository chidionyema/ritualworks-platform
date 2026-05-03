using FluentAssertions;
using Haworks.Orders.Domain;
using Xunit;

namespace Haworks.Orders.Unit;

public sealed class OrderTests
{
    private static IEnumerable<(Guid productId, string productName, int quantity, decimal unitPrice)>
        OneItem() => new[] { (Guid.NewGuid(), "Widget", 1, 10m) };

    private static Order NewOrder(decimal total = 10m, string userId = "user-1") =>
        Order.Create(userId, total, "USD", Guid.NewGuid(), "key-1", "buyer@example.com", OneItem());

    [Fact]
    public void Create_initializes_to_Created_status_with_one_item()
    {
        var order = NewOrder();
        order.Status.Should().Be(OrderStatus.Created);
        order.PaymentId.Should().BeNull();
        order.AbandonReason.Should().BeNull();
        order.Items.Should().ContainSingle();
        order.Items.Single().Quantity.Should().Be(1);
    }

    [Fact]
    public void Create_with_no_items_throws()
    {
        Action act = () => Order.Create("u", 0m, "USD", Guid.NewGuid(), "k", "e@x.com",
            Array.Empty<(Guid, string, int, decimal)>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_userId_throws()
    {
        Action act = () => Order.Create("", 1m, "USD", Guid.NewGuid(), "k", "e@x.com", OneItem());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_sagaId_throws()
    {
        Action act = () => Order.Create("u", 1m, "USD", Guid.Empty, "k", "e@x.com", OneItem());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_negative_total_throws()
    {
        Action act = () => Order.Create("u", -1m, "USD", Guid.NewGuid(), "k", "e@x.com", OneItem());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkPaid_transitions_Created_to_Paid()
    {
        var order = NewOrder();
        var paymentId = Guid.NewGuid();
        order.MarkPaid(paymentId).Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        order.PaymentId.Should().Be(paymentId);
    }

    [Fact]
    public void MarkPaid_returns_false_when_already_Paid()
    {
        var order = NewOrder();
        order.MarkPaid(Guid.NewGuid()).Should().BeTrue();
        order.MarkPaid(Guid.NewGuid()).Should().BeFalse("idempotent: same payment redelivery should noop");
        order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public void MarkPaid_returns_false_when_already_Abandoned()
    {
        var order = NewOrder();
        order.MarkAbandoned("StockReservationFailed").Should().BeTrue();
        order.MarkPaid(Guid.NewGuid()).Should().BeFalse(
            "Abandoned is terminal — a late-arriving PaymentCompleted must not flip it back to Paid");
        order.Status.Should().Be(OrderStatus.Abandoned);
    }

    [Fact]
    public void MarkPaid_with_empty_paymentId_throws()
    {
        var order = NewOrder();
        Action act = () => order.MarkPaid(Guid.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkAbandoned_transitions_Created_to_Abandoned_with_reason()
    {
        var order = NewOrder();
        order.MarkAbandoned("StockReservationFailed: insufficient stock").Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Abandoned);
        order.AbandonReason.Should().Contain("StockReservationFailed");
    }

    [Fact]
    public void MarkAbandoned_returns_false_when_already_Paid()
    {
        var order = NewOrder();
        order.MarkPaid(Guid.NewGuid());
        order.MarkAbandoned("late stock failure").Should().BeFalse(
            "Paid is terminal — a late-arriving StockReservationFailed must not flip it to Abandoned");
        order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public void MarkAbandoned_with_empty_reason_throws()
    {
        var order = NewOrder();
        Action act = () => order.MarkAbandoned("");
        act.Should().Throw<ArgumentException>();
    }
}

public sealed class OrderItemTests
{
    [Fact]
    public void Create_computes_LineTotal()
    {
        var item = OrderItem.Create(Guid.NewGuid(), Guid.NewGuid(), "Widget", 3, 10.50m);
        item.LineTotal.Should().Be(31.50m);
    }

    [Fact]
    public void Create_with_zero_quantity_throws()
    {
        Action act = () => OrderItem.Create(Guid.NewGuid(), Guid.NewGuid(), "Widget", 0, 1m);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_negative_unitPrice_throws()
    {
        Action act = () => OrderItem.Create(Guid.NewGuid(), Guid.NewGuid(), "Widget", 1, -1m);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_productId_throws()
    {
        Action act = () => OrderItem.Create(Guid.NewGuid(), Guid.Empty, "Widget", 1, 1m);
        act.Should().Throw<ArgumentException>();
    }
}
