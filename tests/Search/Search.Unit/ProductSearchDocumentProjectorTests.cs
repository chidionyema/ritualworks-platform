using FluentAssertions;
using Haworks.Search.Application.Indexing;
using Xunit;

namespace Haworks.Search.Unit;

public sealed class ProductSearchDocumentProjectorTests
{
    [Fact]
    public void From_maps_all_fields()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var categoryId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var doc = ProductSearchDocumentProjector.From(
            id, "Pen", "Blue ink", 1.99m,
            isInStock: true, isListed: true,
            categoryId, "Stationery",
            sourceVersion: 7);

        doc.ProductIdKey.Should().Be(id.ToString("N"));
        doc.ProductId.Should().Be(id.ToString());
        doc.Name.Should().Be("Pen");
        doc.Description.Should().Be("Blue ink");
        doc.UnitPrice.Should().Be(1.99m);
        doc.IsInStock.Should().BeTrue();
        doc.IsListed.Should().BeTrue();
        doc.CategoryId.Should().Be(categoryId.ToString());
        doc.CategoryName.Should().Be("Stationery");
        doc.SourceVersion.Should().Be(7);
        doc.IndexedAt.Should().BeGreaterThan(0);
    }

    [Fact]
    public void From_maps_null_category_to_uncategorized()
    {
        var doc = ProductSearchDocumentProjector.From(
            Guid.NewGuid(), "X", "Y", 1m,
            isInStock: false, isListed: true,
            Guid.NewGuid(), categoryName: null,
            sourceVersion: 1);

        doc.CategoryName.Should().Be("Uncategorized");
    }

    [Fact]
    public void From_uses_dash_free_uuid_for_ProductIdKey()
    {
        var id = Guid.NewGuid();

        var doc = ProductSearchDocumentProjector.From(
            id, "X", "Y", 1m,
            isInStock: false, isListed: true,
            Guid.NewGuid(), "Cat",
            sourceVersion: 1);

        doc.ProductIdKey.Should().NotContain("-");
        doc.ProductIdKey.Should().Be(id.ToString("N"));
    }
}
