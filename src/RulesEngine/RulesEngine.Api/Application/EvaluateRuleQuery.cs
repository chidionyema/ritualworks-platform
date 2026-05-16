using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using MediatR;

namespace Haworks.RulesEngine.Api.Application;

public record EvaluateRuleQuery(Guid RuleId, Dictionary<string, object> Inputs) : IRequest<Result<RuleEvaluationResult>>;
