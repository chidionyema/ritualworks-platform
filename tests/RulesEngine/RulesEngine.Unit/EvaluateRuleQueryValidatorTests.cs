using FluentValidation.TestHelper;
using Haworks.RulesEngine.Api.Application;
using Xunit;

namespace Haworks.RulesEngine.Unit;

public class EvaluateRuleQueryValidatorTests
{
    private readonly EvaluateRuleQueryValidator _validator;

    public EvaluateRuleQueryValidatorTests()
    {
        _validator = new EvaluateRuleQueryValidator();
    }

    [Fact]
    public void Should_HaveError_When_RuleIdIsEmpty()
    {
        var query = new EvaluateRuleQuery(string.Empty, new Dictionary<string, object>());
        var result = _validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.RuleId);
    }

    [Fact]
    public void Should_HaveError_When_InputsIsNull()
    {
        var query = new EvaluateRuleQuery("rule-1", null!);
        var result = _validator.TestValidate(query);
        result.ShouldHaveValidationErrorFor(x => x.Inputs);
    }

    [Fact]
    public void Should_NotHaveError_When_QueryIsValid()
    {
        var query = new EvaluateRuleQuery("rule-1", new Dictionary<string, object>());
        var result = _validator.TestValidate(query);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
