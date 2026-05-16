using Haworks.BuildingBlocks.Common;
using Haworks.RulesEngine.Api.Domain;
using Haworks.RulesEngine.Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Haworks.RulesEngine.Api.Application;

public class CreateRuleCommandHandler : IRequestHandler<CreateRuleCommand, Result<Rule>>
{
    private readonly RulesDbContext _db;
    public CreateRuleCommandHandler(RulesDbContext db) => _db = db;

    public async Task<Result<Rule>> Handle(CreateRuleCommand request, CancellationToken cancellationToken)
    {
        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Expression = request.Expression,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Rules.Add(rule);
        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success(rule);
    }
}

public class UpdateRuleCommandHandler : IRequestHandler<UpdateRuleCommand, Result<Rule>>
{
    private readonly RulesDbContext _db;
    public UpdateRuleCommandHandler(RulesDbContext db) => _db = db;

    public async Task<Result<Rule>> Handle(UpdateRuleCommand request, CancellationToken cancellationToken)
    {
        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken);
        if (rule is null)
            return Result.Failure<Rule>(Error.NotFound("RulesEngine.RuleNotFound", $"Rule '{request.Id}' not found."));

        rule.Name = request.Name;
        rule.Expression = request.Expression;
        rule.IsActive = request.IsActive;
        rule.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success(rule);
    }
}

public class DeleteRuleCommandHandler : IRequestHandler<DeleteRuleCommand, Result<bool>>
{
    private readonly RulesDbContext _db;
    public DeleteRuleCommandHandler(RulesDbContext db) => _db = db;

    public async Task<Result<bool>> Handle(DeleteRuleCommand request, CancellationToken cancellationToken)
    {
        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken);
        if (rule is null)
            return Result.Failure<bool>(Error.NotFound("RulesEngine.RuleNotFound", $"Rule '{request.Id}' not found."));

        _db.Rules.Remove(rule);
        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }
}

public class GetRuleQueryHandler : IRequestHandler<GetRuleQuery, Result<Rule>>
{
    private readonly RulesDbContext _db;
    public GetRuleQueryHandler(RulesDbContext db) => _db = db;

    public async Task<Result<Rule>> Handle(GetRuleQuery request, CancellationToken cancellationToken)
    {
        var rule = await _db.Rules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken);

        return rule is null
            ? Result.Failure<Rule>(Error.NotFound("RulesEngine.RuleNotFound", $"Rule '{request.Id}' not found."))
            : Result.Success(rule);
    }
}

public class ListRulesQueryHandler : IRequestHandler<ListRulesQuery, Result<IReadOnlyList<Rule>>>
{
    private readonly RulesDbContext _db;
    public ListRulesQueryHandler(RulesDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<Rule>>> Handle(ListRulesQuery request, CancellationToken cancellationToken)
    {
        var query = _db.Rules.AsNoTracking();
        if (request.ActiveOnly == true)
            query = query.Where(r => r.IsActive);

        var rules = (IReadOnlyList<Rule>)await query
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return Result.Success(rules);
    }
}
