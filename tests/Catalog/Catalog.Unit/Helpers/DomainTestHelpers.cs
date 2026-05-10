using Haworks.Catalog.Domain;

namespace Haworks.Catalog.UnitTests.Helpers;

public static class DomainTestHelpers
{
    public static Product CreateProduct(
        string name = "Test Product",
        string description = "Test Description",
        decimal price = 10.99m,
        Guid? categoryId = null,
        int stock = 100)
    {
        var product = Product.Create(name, description, price, categoryId ?? Guid.NewGuid());
        product.RestockTo(stock);
        return product;
    }

    public static Category CreateCategory(
        string name = "Test Category",
        string? description = "Test Description")
    {
        return Category.Create(name, description);
    }

    public static ProductReview CreateReview(
        Guid productId,
        string userId = "user-123",
        int rating = 5,
        string content = "Great product!",
        string? title = "Awesome",
        string? authorName = "John Doe")
    {
        return ProductReview.Create(productId, userId, rating, content, authorName, title);
    }
}
