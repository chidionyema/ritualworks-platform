using FluentValidation;

namespace Haworks.RulesEngine.Api.Application;

public class DeleteRuleCommandValidator : AbstractValidator<DeleteRuleCommand>
{
    public DeleteRuleCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}

public class GetRuleQueryValidator : AbstractValidator<GetRuleQuery>
{
    public GetRuleQueryValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}

public class ListRulesQueryValidator : AbstractValidator<ListRulesQuery>
{
    public ListRulesQueryValidator()
    {
    }
}
