using FluentAssertions;
using FluentValidation.TestHelper;
using Haworks.Merchant.Application.Merchants.Commands.CreateMerchant;
using Haworks.Merchant.Application.Merchants.Commands.RejectMerchant;
using Haworks.Merchant.Application.Merchants.Commands.UpdateMerchant;
using Xunit;

namespace Haworks.Merchant.Unit.Application;

public sealed class ValidatorTests
{
    [Fact]
    public void CreateMerchant_invalid_slug_fails()
    {
        var validator = new CreateMerchantCommandValidator();
        var command = new CreateMerchantCommand(Guid.NewGuid(), "Name", "INVALID SLUG!");

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Slug);
    }

    [Fact]
    public void CreateMerchant_empty_name_fails()
    {
        var validator = new CreateMerchantCommandValidator();
        var command = new CreateMerchantCommand(Guid.NewGuid(), "", "valid-slug");

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Name);
    }

    [Fact]
    public void CreateMerchant_valid_command_passes()
    {
        var validator = new CreateMerchantCommandValidator();
        var command = new CreateMerchantCommand(Guid.NewGuid(), "My Shop", "my-shop");

        var result = validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateMerchant_invalid_email_fails()
    {
        var validator = new UpdateMerchantCommandValidator();
        var command = new UpdateMerchantCommand(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, "not-an-email", null, null, null);

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.ContactEmail);
    }

    [Fact]
    public void UpdateMerchant_invalid_url_fails()
    {
        var validator = new UpdateMerchantCommandValidator();
        var command = new UpdateMerchantCommand(Guid.NewGuid(), Guid.NewGuid(), null, null, "not a url", null, null, null, null, null);

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.LogoUrl);
    }

    [Fact]
    public void UpdateMerchant_name_exceeds_max_length_fails()
    {
        var validator = new UpdateMerchantCommandValidator();
        var command = new UpdateMerchantCommand(Guid.NewGuid(), Guid.NewGuid(), new string('a', 201), null, null, null, null, null, null, null);

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Name);
    }

    [Fact]
    public void RejectMerchant_empty_reason_fails()
    {
        var validator = new RejectMerchantCommandValidator();
        var command = new RejectMerchantCommand(Guid.NewGuid(), "admin-1", "");

        var result = validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Reason);
    }
}
