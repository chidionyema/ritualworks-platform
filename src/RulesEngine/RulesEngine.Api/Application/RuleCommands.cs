using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using MediatR;

namespace Haworks.RulesEngine.Api.Application;

public record CreateRuleCommand(string Name, string Expression) : IRequest<Result<Rule>>;

public record UpdateRuleCommand(Guid Id, string Name, string Expression, bool IsActive) : IRequest<Result<Rule>>;

public record DeleteRuleCommand(Guid Id) : IRequest<Result<bool>>;

public record GetRuleQuery(Guid Id) : IRequest<Result<Rule>>;

public record ListRulesQuery(bool? ActiveOnly) : IRequest<Result<IReadOnlyList<Rule>>>;
