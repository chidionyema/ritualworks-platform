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
    private readonly FeatureFlagsDbContext _db;

    public EvaluateFlagHandler(FeatureFlagsDbContext db)
    {
        _db = db;
    }

    public async Task<Result<bool>> Handle(EvaluateFlagQuery request, CancellationToken ct)
    {
        var flag = await _db.FeatureFlags
            .Include(x => x.Rules)
            .FirstOrDefaultAsync(x => x.Name == request.FlagName, ct);

        if (flag == null) return Result.Failure<bool>(Error.NotFound("Flag.NotFound", $"Flag {request.FlagName} not found"));
        if (!flag.IsEnabled) return Result.Success(false);

        foreach (var rule in flag.Rules)
        {
            if (rule.UserId == request.UserId) return Result.Success(true);
            if (rule.Region == request.Region) return Result.Success(true);
            if (rule.PercentageRollout.HasValue)
            {
                var hash = request.UserId.GetHashCode();
                if (Math.Abs(hash % 100) < rule.PercentageRollout.Value) return Result.Success(true);
            }
        }

        return Result.Success(false);
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
        await _publishEndpoint.Publish(new FeatureFlagUpdated(flag.Name, flag.IsEnabled), ct);

        return Result<Unit>.Success(Unit.Value);
    }
}
.Success(Unit.Value);
    }
}
