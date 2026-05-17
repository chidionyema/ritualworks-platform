using System.Text.Json.Serialization;
using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using MediatR;

namespace Haworks.RulesEngine.Api.Application;

public record EvaluateRuleQuery(
    [property: JsonRequired] Guid RuleId, 
    [property: JsonRequired] Dictionary<string, object> Inputs) : IRequest<Result<RuleEvaluationResult>>;
