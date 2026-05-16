using System.Diagnostics;
using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.RulesEngine.Api.Application;

public class EvaluateRuleQueryHandler : IRequestHandler<EvaluateRuleQuery, Result<RuleEvaluationResult>>
{
    private readonly IRulesEvaluator _rulesEvaluator;
    private readonly ILogger<EvaluateRuleQueryHandler> _logger;

    public EvaluateRuleQueryHandler(IRulesEvaluator rulesEvaluator, ILogger<EvaluateRuleQueryHandler> logger)
    {
        _rulesEvaluator = rulesEvaluator;
        _logger = logger;
    }

    public async Task<Result<RuleEvaluationResult>> Handle(EvaluateRuleQuery request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _rulesEvaluator.EvaluateAsync(request.RuleId, request.Inputs, cts.Token);
            sw.Stop();

            if (result.IsSuccess)
            {
                var trace = new RuleTrace(
                    request.RuleId,
                    result.Value.Outcome,
                    request.Inputs,
                    sw.Elapsed,
                    DateTime.UtcNow);

                _logger.LogInformation("RuleTrace: {@Trace}", trace);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<RuleEvaluationResult>(
                Error.Timeout("RulesEngine.Timeout", "Rule evaluation timed out"));
        }
    }
}
