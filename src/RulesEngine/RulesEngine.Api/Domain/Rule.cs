using Haworks.BuildingBlocks.Common;

namespace Haworks.RulesEngine.Api.Domain;

public class Rule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
}

public interface IRulesEvaluator
{
    Task<Result<bool>> EvaluateAsync(string expression, Dictionary<string, object> inputs, CancellationToken cancellationToken);
}
