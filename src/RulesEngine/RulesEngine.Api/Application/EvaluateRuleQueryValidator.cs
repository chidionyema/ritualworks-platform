using FluentValidation;

namespace Haworks.RulesEngine.Api.Application;

public class EvaluateRuleQueryValidator : AbstractValidator<EvaluateRuleQuery>
{
    public EvaluateRuleQueryValidator()
    {
        RuleFor(x => x.RuleId).NotEmpty();
        RuleFor(x => x.Inputs).NotNull();
    }
}
