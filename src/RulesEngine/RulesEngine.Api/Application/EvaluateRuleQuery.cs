using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.RulesEngine.Api.Application;

public record EvaluateRuleQuery(string RuleId, Dictionary<string, object> Inputs) : IRequest<Result<bool>>;
