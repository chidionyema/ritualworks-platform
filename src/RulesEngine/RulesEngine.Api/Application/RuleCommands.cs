using System.Text.Json.Serialization;
using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.RulesEngine.Api.Domain;
using MediatR;

namespace Haworks.RulesEngine.Api.Application;

public record CreateRuleCommand(
    [property: JsonRequired] string Name, 
    [property: JsonRequired] string Expression,
    string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<Rule>>;

public record UpdateRuleCommand(
    [property: JsonRequired] Guid Id, 
    [property: JsonRequired] string Name, 
    [property: JsonRequired] string Expression, 
    [property: JsonRequired] bool IsActive,
    string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<Rule>>;

public record DeleteRuleCommand(Guid Id, string IdempotencyKey = "") : IIdempotentCommand, IRequest<Result<bool>>;

public record GetRuleQuery(Guid Id) : IRequest<Result<Rule>>;

public record ListRulesQuery(bool? ActiveOnly) : IRequest<Result<IReadOnlyList<Rule>>>;
