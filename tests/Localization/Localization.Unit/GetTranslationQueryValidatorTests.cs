using FluentValidation.TestHelper;
using Haworks.Localization.Api.Application;
using Xunit;

namespace Haworks.Localization.Unit;

public class GetTranslationQueryValidatorTests
{
    private readonly GetTranslationQueryValidator _validator;

    public GetTranslationQueryValidatorTests()
    {
        _validator = new GetTranslationQueryValidator();
    }

    [Fact]
    public void Should_HaveError_When_KeyIsEmpty()
    {
        var query = new GetTranslationQuery("", "en-US");
        var result = _validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Key);
    }

    [Fact]
    public void Should_HaveError_When_LocaleIsEmpty()
    {
        var query = new GetTranslationQuery("welcome", "");
        var result = _validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Locale);
    }

    [Fact]
    public void Should_NotHaveError_When_Valid()
    {
        var query = new GetTranslationQuery("welcome", "en-US");
        var result = _validator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("zh-Hans")]
    [InlineData("pt-BR")]
    public void Should_NotHaveError_When_LocaleFormatIsValid(string locale)
    {
        var query = new GetTranslationQuery("welcome", locale);
        var result = _validator.TestValidate(query);
        result.ShouldNotHaveValidationErrorFor(x => x.Locale);
    }

    [Theory]
    [InlineData("e")]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("EN-US")]
    [InlineData("123")]
    [InlineData("en-")]
    [InlineData("-US")]
    public void Should_HaveError_When_LocaleFormatIsInvalid(string locale)
    {
        var query = new GetTranslationQuery("welcome", locale);
        var result = _validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Locale);
    }
}

public class UpsertTranslationCommandValidatorTests
{
    private readonly UpsertTranslationCommandValidator _validator = new();

    [Fact]
    public void Should_NotHaveError_When_Valid()
    {
        var command = new UpsertTranslationCommand("welcome", "en-US", "Welcome!", "user@test.com");
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("e")]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("EN-US")]
    [InlineData("123")]
    public void Should_HaveError_When_LocaleFormatIsInvalid(string locale)
    {
        var command = new UpsertTranslationCommand("welcome", locale, "Hello", "user@test.com");
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Locale);
    }

    [Fact]
    public void Should_HaveError_When_KeyIsEmpty()
    {
        var command = new UpsertTranslationCommand("", "en-US", "Hello", "user@test.com");
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Key);
    }

    [Fact]
    public void Should_HaveError_When_ValueIsEmpty()
    {
        var command = new UpsertTranslationCommand("welcome", "en-US", "", "user@test.com");
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Value);
    }
}
