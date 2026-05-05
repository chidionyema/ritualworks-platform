using FluentValidation.TestHelper;
using Haworks.Catalog.Application.Commands;
using Haworks.Catalog.Application.Validators;
using Xunit;

namespace Haworks.Catalog.Unit.Validators.Products;

public class CreateProductCommandValidatorTests
{
    private readonly CreateProductCommandValidator _validator = new();

    private static CreateProductCommand CreateValidCommand() => new(
        Name: "Test Product",
        Description: "Test Description",
        UnitPrice: 99.99m,
        CategoryId: Guid.NewGuid(),
        InitialStock: 100
    );

    [Fact]
    public void Validate_WithValidCommand_ShouldNotHaveErrors()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    #region Name Validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithEmptyName_ShouldHaveError(string? name)
    {
        var command = CreateValidCommand() with { Name = name! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_WithNameTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Name = new string('a', 201) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    #endregion

    #region Description Validation

    [Fact]
    public void Validate_WithNullDescription_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Description = null! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Validate_WithDescriptionTooLong_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Description = new string('a', 4001) };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    #endregion

    #region Price Validation

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-100)]
    public void Validate_WithNegativePrice_ShouldHaveError(decimal price)
    {
        var command = CreateValidCommand() with { UnitPrice = price };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UnitPrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(1000)]
    public void Validate_WithValidPrice_ShouldNotHaveError(decimal price)
    {
        var command = CreateValidCommand() with { UnitPrice = price };
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.UnitPrice);
    }

    #endregion

    #region Stock Validation

    [Theory]
    [InlineData(-1)]
    public void Validate_WithNegativeStock_ShouldHaveError(int stock)
    {
        var command = CreateValidCommand() with { InitialStock = stock };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.InitialStock);
    }

    #endregion

    #region CategoryId Validation

    [Fact]
    public void Validate_WithEmptyCategoryId_ShouldHaveError()
    {
        var command = CreateValidCommand() with { CategoryId = Guid.Empty };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.CategoryId);
    }

    #endregion
}
