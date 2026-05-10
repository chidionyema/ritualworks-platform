using FluentAssertions;
using Haworks.Catalog.Domain;
using Xunit;

namespace Haworks.Catalog.Unit.Domain;

public class ProductTests
{
    #region Factory Method Tests

    [Fact]
    public void Create_WithValidParameters_ReturnsProduct()
    {
        // Arrange
        var name = "Test Product";
        var description = "Description";
        var price = 29.99m;
        var categoryId = Guid.NewGuid();

        // Act
        var product = Product.Create(name, description, price, categoryId);

        // Assert
        product.Id.Should().NotBeEmpty();
        product.Name.Should().Be(name);
        product.Description.Should().Be(description);
        product.UnitPrice.Should().Be(price);
        product.CategoryId.Should().Be(categoryId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidName_ThrowsArgumentException(string? name)
    {
        // Arrange
        var categoryId = Guid.NewGuid();

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => Product.Create(name!, "Description", 29.99m, categoryId));
    }

    [Fact]
    public void Create_WithNegativePrice_ThrowsArgumentException()
    {
        // Arrange
        var categoryId = Guid.NewGuid();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => Product.Create("Test", "Description", -10m, categoryId));
    }

    #endregion

    #region Default Values Tests

    [Fact]
    public void Create_DefaultValuesAreSet()
    {
        // Act
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Assert
        product.IsListed.Should().BeFalse();
        product.IsInStock.Should().BeFalse();
        product.StockQuantity.Should().Be(0);
    }

    #endregion

    #region Pricing Tests

    [Fact]
    public void UpdatePricing_SetsUnitPrice()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act
        product.UpdatePricing(49.99m);

        // Assert
        product.UnitPrice.Should().Be(49.99m);
    }

    [Fact]
    public void UpdatePricing_WithNegativePrice_ThrowsArgumentException()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act & Assert
        Assert.Throws<ArgumentException>(() => product.UpdatePricing(-10m));
    }

    [Fact]
    public void UpdatePricing_WithZeroPrice_Succeeds()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act
        product.UpdatePricing(0m);

        // Assert
        product.UnitPrice.Should().Be(0m);
    }

    #endregion

    #region Stock Management Tests

    [Fact]
    public void RestockTo_SetsQuantityAndInStock()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act
        product.RestockTo(100);

        // Assert
        product.StockQuantity.Should().Be(100);
        product.IsInStock.Should().BeTrue();
    }

    [Fact]
    public void RestockTo_ToZero_SetsOutOfStock()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        product.RestockTo(50);

        // Act
        product.RestockTo(0);

        // Assert
        product.StockQuantity.Should().Be(0);
        product.IsInStock.Should().BeFalse();
    }

    [Fact]
    public void RestockTo_WithNegativeQuantity_ThrowsArgumentException()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act & Assert
        Assert.Throws<ArgumentException>(() => product.RestockTo(-10));
    }

    [Fact]
    public void ReserveStock_ReducesQuantity()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        product.RestockTo(10);

        // Act
        var result = product.ReserveStock(3);

        // Assert
        result.Should().BeTrue();
        product.StockQuantity.Should().Be(7);
        product.IsInStock.Should().BeTrue();
    }

    [Fact]
    public void ReserveStock_DepleteAllStock_SetsOutOfStock()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        product.RestockTo(5);

        // Act
        var result = product.ReserveStock(5);

        // Assert
        result.Should().BeTrue();
        product.StockQuantity.Should().Be(0);
        product.IsInStock.Should().BeFalse();
    }

    [Fact]
    public void ReserveStock_InsufficientStock_ReturnsFalse()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        product.RestockTo(5);

        // Act
        var result = product.ReserveStock(10);

        // Assert
        result.Should().BeFalse();
        product.StockQuantity.Should().Be(5);
    }

    [Fact]
    public void ReleaseStock_IncreasesQuantity()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        product.RestockTo(5);

        // Act
        product.ReleaseStock(10);

        // Assert
        product.StockQuantity.Should().Be(15);
        product.IsInStock.Should().BeTrue();
    }

    #endregion

    #region Listing Status Tests

    [Fact]
    public void List_SetsIsListedTrue()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act
        product.List();

        // Assert
        product.IsListed.Should().BeTrue();
    }

    [Fact]
    public void Unlist_SetsIsListedFalse()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        product.List();

        // Act
        product.Unlist();

        // Assert
        product.IsListed.Should().BeFalse();
    }

    #endregion

    #region Basic Info Tests

    [Fact]
    public void UpdateBasicInfo_UpdatesFields()
    {
        // Arrange
        var product = Product.Create("Old Name", "Old Desc", 10m, Guid.NewGuid());

        // Act
        product.UpdateBasicInfo("New Name", "New Description");

        // Assert
        product.Name.Should().Be("New Name");
        product.Description.Should().Be("New Description");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateBasicInfo_WithInvalidName_ThrowsArgumentException(string? name)
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => product.UpdateBasicInfo(name!, "Description"));
    }

    #endregion

    #region Collections Tests

    [Fact]
    public void AddMetadata_AddsToCollection()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act
        product.AddMetadata("Color", "Blue");

        // Assert
        product.Metadata.Should().HaveCount(1);
        product.Metadata.First().KeyName.Should().Be("Color");
        product.Metadata.First().KeyValue.Should().Be("Blue");
    }

    [Fact]
    public void AddMetadata_UpdateExisting_UpdatesValue()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());
        product.AddMetadata("Color", "Blue");

        // Act
        product.AddMetadata("Color", "Red");

        // Assert
        product.Metadata.Should().HaveCount(1);
        product.Metadata.First().KeyValue.Should().Be("Red");
    }

    [Fact]
    public void AddSpecification_AddsToCollection()
    {
        // Arrange
        var product = Product.Create("Test", "Desc", 10m, Guid.NewGuid());

        // Act
        product.AddSpecification("Weight", "100g", 1);

        // Assert
        product.Specifications.Should().HaveCount(1);
        product.Specifications.First().Name.Should().Be("Weight");
        product.Specifications.First().Value.Should().Be("100g");
        product.Specifications.First().DisplayOrder.Should().Be(1);
    }

    #endregion
}
