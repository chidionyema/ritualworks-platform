using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;

namespace Haworks.RulesEngine.Api.Infrastructure;

public class RulesEvaluator : IRulesEvaluator
{
    public async Task<Result<bool>> EvaluateAsync(string expression, Dictionary<string, object> inputs, CancellationToken cancellationToken)
    {
        // Mock AST evaluation
        await Task.Delay(100, cancellationToken); // Simulate work

        if (expression.Contains("age > 18"))
        {
            if (inputs.TryGetValue("age", out var ageValue) && ageValue is int age)
            {
                return Result.Success(age > 18);
            }
            if (inputs.TryGetValue("age", out var ageStr) && int.TryParse(ageStr.ToString(), out int ageInt))
            {
                return Result.Success(ageInt > 18);
            }
        }

        return Result.Success(true); // Default mock
    }
}
