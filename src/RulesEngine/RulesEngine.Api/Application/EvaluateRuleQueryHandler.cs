using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.RulesEngine.Api.Application;

public class EvaluateRuleQueryHandler : IRequestHandler<EvaluateRuleQuery, Result<bool>>
{
    private readonly IRulesEvaluator _rulesEvaluator;
    private readonly ILogger<EvaluateRuleQueryHandler> _logger;

    public EvaluateRuleQueryHandler(IRulesEvaluator rulesEvaluator, ILogger<EvaluateRuleQueryHandler> logger)
    {
        _rulesEvaluator = rulesEvaluator;
        _logger = logger;
    }

    public async Task<Result<bool>> Handle(EvaluateRuleQuery request, CancellationToken cancellationToken)
    {
        // Strict timeout implementation
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            _logger.LogInformation("Evaluating rule {RuleId}", request.RuleId);
            
            // In a real app, we'd fetch the rule from a repository
            var expression = "input.age > 18"; 

            var result = await _rulesEvaluator.EvaluateAsync(expression, request.Inputs, cts.Token);

            if (result.IsSuccess)
            {
                _logger.LogInformation("Rule {RuleId} evaluated to {Result}", request.RuleId, result.Value);
                // Mock TraceLog to Audit
                _logger.LogInformation("TRACE: Rule={RuleId}, Result={Result}, Inputs={Inputs}", 
                    request.RuleId, result.Value, string.Join(",", request.Inputs.Select(kv => $"{kv.Key}={kv.Value}")));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<bool>(Error.Timeout("RulesEngine.Timeout", "Rule evaluation timed out"));
        }
    }
}
