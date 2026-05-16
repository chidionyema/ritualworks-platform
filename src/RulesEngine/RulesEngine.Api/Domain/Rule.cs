using Haworks.BuildingBlocks.Common;

namespace Haworks.RulesEngine.Api.Domain;

public class Rule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public interface IRulesEvaluator
{
    Task<Result<RuleEvaluationResult>> EvaluateAsync(Guid ruleId, Dictionary<string, object> inputs, CancellationToken cancellationToken);
}

public sealed record RuleEvaluationResult(bool Outcome, string Expression, string Trace);
