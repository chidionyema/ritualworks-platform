using FluentValidation;

namespace Haworks.RulesEngine.Api.Application;

// Forbidden SQL injection tokens — kept in sync with RulesEvaluator
internal static class ExpressionRules
{
    internal static readonly string[] ForbiddenTokens =
    [
        "--", ";", "DROP", "DELETE", "INSERT", "UPDATE", "EXEC", "EXECUTE",
        "SELECT", "UNION", "xp_", "sp_", "CAST(", "CONVERT(", "CHAR(",
        "NCHAR(", "VARCHAR(", "DECLARE"
    ];
}

public class CreateRuleCommandValidator : AbstractValidator<CreateRuleCommand>
{
    public CreateRuleCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Expression)
            .NotEmpty()
            .MaximumLength(4000)
            .Must(expr => !ExpressionRules.ForbiddenTokens.Any(t =>
                expr.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Expression contains a forbidden token.");
    }
}

public class UpdateRuleCommandValidator : AbstractValidator<UpdateRuleCommand>
{
    public UpdateRuleCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Expression)
            .NotEmpty()
            .MaximumLength(4000)
            .Must(expr => !ExpressionRules.ForbiddenTokens.Any(t =>
                expr.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Expression contains a forbidden token.");
    }
}
