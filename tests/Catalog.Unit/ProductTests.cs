using FluentAssertions;
using Haworks.Catalog.Domain;
using Xunit;

namespace Haworks.Catalog.Unit;

/// <summary>
/// Pure-domain tests for Product invariants — no DB, no DI, no MassTransit.
/// Catches regressions in stock semantics independent of EF / Postgres /
/// xmin concurrency (which is exercised by the integration tests).
/// </summary>
public sealed class ProductTests
{
    [Fact]
    public void Create_with_negative_price_throws()
    {
        Action act = () => Product.Create("p", "d", -1m, Guid.NewGuid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_name_throws()
    {
        Action act = () => Product.Create("", "d", 1m, Guid.NewGuid());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Newly_created_product_is_not_listed_and_has_zero_stock()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.IsListed.Should().BeFalse();
        p.IsInStock.Should().BeFalse();
        p.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void RestockTo_sets_quantity_and_in_stock_flag()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.RestockTo(5);
        p.StockQuantity.Should().Be(5);
        p.IsInStock.Should().BeTrue();
    }

    [Fact]
    public void RestockTo_zero_marks_out_of_stock()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.RestockTo(5);
        p.RestockTo(0);
        p.IsInStock.Should().BeFalse();
        p.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void RestockTo_negative_throws()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        Action act = () => p.RestockTo(-1);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReserveStock_decrements_when_enough_available()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.RestockTo(10);
        p.ReserveStock(3).Should().BeTrue();
        p.StockQuantity.Should().Be(7);
        p.IsInStock.Should().BeTrue();
    }

    [Fact]
    public void ReserveStock_returns_false_when_insufficient()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.RestockTo(2);
        p.ReserveStock(5).Should().BeFalse();
        p.StockQuantity.Should().Be(2, "failed reservation must not mutate state");
    }

    [Fact]
    public void ReserveStock_to_exactly_zero_marks_out_of_stock()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.RestockTo(3);
        p.ReserveStock(3).Should().BeTrue();
        p.StockQuantity.Should().Be(0);
        p.IsInStock.Should().BeFalse();
    }

    [Fact]
    public void ReserveStock_with_zero_or_negative_throws()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.RestockTo(5);
        ((Action)(() => p.ReserveStock(0))).Should().Throw<ArgumentException>();
        ((Action)(() => p.ReserveStock(-1))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReleaseStock_returns_quantity_to_inventory()
    {
        var p = Product.Create("p", "d", 1m, Guid.NewGuid());
        p.RestockTo(5);
        p.ReserveStock(3).Should().BeTrue();
        p.ReleaseStock(2);
        p.StockQuantity.Should().Be(4);
        p.IsInStock.Should().BeTrue();
    }
}
