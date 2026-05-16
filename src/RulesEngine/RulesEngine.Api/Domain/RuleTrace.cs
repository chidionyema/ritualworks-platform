namespace Haworks.RulesEngine.Api.Domain;

public sealed record RuleTrace(
    Guid RuleId,
    bool Result,
    Dictionary<string, object> Inputs,
    TimeSpan Duration,
    DateTime OccurredAt
);
