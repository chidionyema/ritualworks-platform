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
}
