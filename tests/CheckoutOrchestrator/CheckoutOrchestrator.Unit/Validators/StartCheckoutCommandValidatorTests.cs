using FluentValidation.TestHelper;
using Haworks.CheckoutOrchestrator.Application.Commands;
using Haworks.CheckoutOrchestrator.Application.Validators;
using Haworks.Contracts.Checkout;
using Xunit;

namespace Haworks.CheckoutOrchestrator.Unit.Validators;

public class StartCheckoutCommandValidatorTests
{
    private readonly StartCheckoutCommandValidator _validator = new();

    private static StartCheckoutCommand CreateValidCommand() => new(
        SagaId: Guid.NewGuid(),
        OrderId: Guid.NewGuid(),
        UserId: "user-123",
        CustomerEmail: "test@example.com",
        TotalAmountCents: 10000L,
        IdempotencyKey: "key-123",
        Items: new List<CheckoutItemData>
        {
            new() { ProductId = Guid.NewGuid(), ProductName = "Product 1", Quantity = 1, UnitPriceCents = 10000L }
        }
    );

    [Fact]
    public void Validate_WithValidCommand_ShouldNotHaveErrors()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithEmptyUserId_ShouldHaveError()
    {
        var command = CreateValidCommand() with { UserId = "" };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-email")]
    public void Validate_WithInvalidEmail_ShouldHaveError(string? email)
    {
        var command = CreateValidCommand() with { CustomerEmail = email! };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.CustomerEmail);
    }

    [Fact]
    public void Validate_WithNegativeAmount_ShouldHaveError()
    {
        var command = CreateValidCommand() with { TotalAmountCents = -1L };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.TotalAmountCents);
    }

    [Fact]
    public void Validate_WithEmptyItems_ShouldHaveError()
    {
        var command = CreateValidCommand() with { Items = new List<CheckoutItemData>() };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_WithTooManyItems_ShouldHaveError()
    {
        var items = Enumerable.Range(0, 101).Select(_ => new CheckoutItemData
        {
            ProductId = Guid.NewGuid(),
            ProductName = "P",
            Quantity = 1,
            UnitPriceCents = 1L
        }).ToList();

        var command = CreateValidCommand() with { Items = items };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Items);
    }

    [Fact]
    public void Validate_WithInvalidItem_ShouldHaveError()
    {
        var command = CreateValidCommand() with 
        { 
            Items = new List<CheckoutItemData> 
            { 
                new() { ProductId = Guid.Empty, ProductName = "", Quantity = 0, UnitPriceCents = -1L } 
            } 
        };
        var result = _validator.TestValidate(command);
        result.ShouldHaveAnyValidationError();
    }
}
