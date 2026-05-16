using Haworks.BuildingBlocks.Common;
using Haworks.FeatureFlags.Api.Domain;
using Haworks.FeatureFlags.Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using MassTransit;
using Haworks.Contracts.FeatureFlags;

namespace Haworks.FeatureFlags.Api.Application;

public record EvaluateFlagQuery(string FlagName, string UserId, string Region) : IRequest<Result<bool>>;

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

public record UpdateFlagCommand(string FlagName, bool IsEnabled, string Description) : IRequest<Result<Unit>>;

public class UpdateFlagValidator : AbstractValidator<UpdateFlagCommand>
{
    public UpdateFlagValidator()
    {
        RuleFor(x => x.FlagName).NotEmpty();
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

        await _db.SaveChangesAsync(ct);
        await _publishEndpoint.Publish(new FeatureFlagUpdated { FlagName = flag.Name, IsEnabled = flag.IsEnabled }, ct);

        return Result.Success(Unit.Value);
    }
}
