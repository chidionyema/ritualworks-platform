using FluentValidation.TestHelper;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.Validators;
using Xunit;

namespace Haworks.Catalog.Unit.Validators.Products;

public class UpdateProductCommandValidatorTests
{
    private readonly UpdateProductCommandValidator _validator = new();

    private static UpdateProductCommand CreateValidCommand() => new(
        ProductId: Guid.NewGuid(),
        Name: "Updated Product",
        Description: "Updated Description",
        UnitPrice: 150.00m,
        CategoryId: Guid.NewGuid(),
        IsListed: true
    );

    [Fact]
    public void Validate_WithValidCommand_ShouldNotHaveErrors()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithEmptyProductId_ShouldHaveError()
    {
        var command = CreateValidCommand() with { ProductId = Guid.Empty };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.ProductId);
    }

    [Fact]
    public void Validate_WithEmptyName_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Name = "" };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }
}

public class CreateProductReviewCommandValidatorTests
{
    private readonly CreateProductReviewCommandValidator _validator = new();

    private static CreateProductReviewCommand CreateValidCommand() => new(
        ProductId: Guid.NewGuid(),
        Title: "Great Product",
        Content: "This is a very good product, I like it a lot.",
        Rating: 5,
        UserId: "user-123",
        AuthorName: "John Doe",
        IsAdmin: false
    );

    [Fact]
    public void Validate_WithValidCommand_ShouldNotHaveErrors()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithLowRating_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Rating = 0 };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Rating);
    }

    [Fact]
    public void Validate_WithShortContent_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Content = "Too short" };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Content);
    }
}
