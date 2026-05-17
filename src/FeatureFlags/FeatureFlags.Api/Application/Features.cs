using System.Text.Json.Serialization;
using Haworks.BuildingBlocks.Common;
using Haworks.FeatureFlags.Api.Domain;
using Haworks.FeatureFlags.Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using MassTransit;
using Haworks.Contracts.FeatureFlags;

namespace Haworks.FeatureFlags.Api.Application;

public record EvaluateFlagQuery(
    [property: JsonRequired] string FlagName,
    [property: JsonRequired] string UserId,
    string Region) : IRequest<Result<bool>>;

public class EvaluateFlagValidator : AbstractValidator<EvaluateFlagQuery>
{
    public EvaluateFlagValidator()
    {
        RuleFor(x => x.FlagName).NotEmpty();
        RuleFor(x => x.UserId).NotEmpty();
    }
}

public class EvaluateFlagHandler : IRequestHandler<EvaluateFlagQuery, Result<bool>>
{
    private readonly IFeatureFlagCache _cache;

    public EvaluateFlagHandler(IFeatureFlagCache cache)
    {
        _cache = cache;
    }

    public Task<Result<bool>> Handle(EvaluateFlagQuery request, CancellationToken ct)
    {
        // Staff-level hardening: Zero DB hits during evaluation.
        // Logic moved to in-memory IFeatureFlagCache synchronized via MassTransit.
        var result = _cache.Evaluate(request.FlagName, request.UserId, request.Region);
        return Task.FromResult(Result.Success(result));
    }
}

public record UpdateFlagCommand(
    [property: JsonRequired] string FlagName,
    [property: JsonRequired] bool IsEnabled,
    string Description,
    List<FeatureFlagRule>? Rules = null) : IRequest<Result<Unit>>;

public class UpdateFlagValidator : AbstractValidator<UpdateFlagCommand>
{
    public UpdateFlagValidator()
    {
        RuleFor(x => x.FlagName).NotEmpty();
        RuleFor(x => x.Rules)
            .Must(rules => rules == null || rules.Count <= 20)
            .WithMessage("A flag cannot have more than 20 rules.");
    }
}

public class UpdateFlagHandler : IRequestHandler<UpdateFlagCommand, Result<Unit>>
{
    private readonly FeatureFlagsDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;

    public UpdateFlagHandler(FeatureFlagsDbContext db, IPublishEndpoint publishEndpoint)
    {
        _db = db;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Result<Unit>> Handle(UpdateFlagCommand request, CancellationToken ct)
    {
        var flag = await _db.FeatureFlags.FirstOrDefaultAsync(x => x.Name == request.FlagName, ct);
        if (flag == null)
        {
            flag = new FeatureFlag { Name = request.FlagName, IsEnabled = request.IsEnabled, Description = request.Description };
            _db.FeatureFlags.Add(flag);
        }
        else
        {
            flag.IsEnabled = request.IsEnabled;
            flag.Description = request.Description;
        }

        // Outbox pattern: publish BEFORE SaveChangesAsync so the message is written
        // in the same transaction via the EF Core outbox. Atomicity guaranteed.
        await _publishEndpoint.Publish(new FeatureFlagUpdated { FlagName = flag.Name, IsEnabled = flag.IsEnabled }, ct);
        await _db.SaveChangesAsync(ct);

        return Result.Success(Unit.Value);
    }
}

// --- Delete flag command with immutability guard ---

public record DeleteFlagCommand(
    [property: JsonRequired] string FlagName) : IRequest<Result<Unit>>;

public class DeleteFlagValidator : AbstractValidator<DeleteFlagCommand>
{
    public DeleteFlagValidator()
    {
        RuleFor(x => x.FlagName).NotEmpty();
    }
}

public class DeleteFlagHandler : IRequestHandler<DeleteFlagCommand, Result<Unit>>
{
    private readonly FeatureFlagsDbContext _db;

    public DeleteFlagHandler(FeatureFlagsDbContext db)
    {
        _db = db;
    }

    public async Task<Result<Unit>> Handle(DeleteFlagCommand request, CancellationToken ct)
    {
        var flag = await _db.FeatureFlags
            .Include(f => f.Rules)
            .FirstOrDefaultAsync(x => x.Name == request.FlagName, ct);

        if (flag == null)
            return Result.Failure<Unit>(new Error("Flag.NotFound", $"Flag '{request.FlagName}' not found."));

        // Immutability guard: cannot delete an enabled flag — must disable first
        if (flag.IsEnabled)
            return Result.Failure<Unit>(new Error("Flag.StillEnabled", "Cannot delete an enabled flag. Disable it first."));

        _db.Rules.RemoveRange(flag.Rules);
        _db.FeatureFlags.Remove(flag);
        await _db.SaveChangesAsync(ct);

        return Result.Success(Unit.Value);
    }
}
