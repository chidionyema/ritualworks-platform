using System.Diagnostics;
using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using MediatR;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
        var sw = Stopwatch.StartNew();

        // Strict timeout implementation
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            // In a real app, we'd fetch the rule from a repository
            var expression = "input.age > 18"; 

            var result = await _rulesEvaluator.EvaluateAsync(expression, request.Inputs, cts.Token);

            sw.Stop();

            if (result.IsSuccess)
            {
                // Staff-level hardening: Structured Traceability. 
                // This payload can be serialized and pushed to the Analytics ingestion pipeline.
                var trace = new RuleTrace(
                    request.RuleId,
                    result.Value,
                    request.Inputs,
                    sw.Elapsed,
                    DateTime.UtcNow
                );

                _logger.LogInformation("RuleTrace: {Trace}", JsonSerializer.Serialize(trace));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<bool>(Error.Timeout("RulesEngine.Timeout", "Rule evaluation timed out"));
        }
    }
}
