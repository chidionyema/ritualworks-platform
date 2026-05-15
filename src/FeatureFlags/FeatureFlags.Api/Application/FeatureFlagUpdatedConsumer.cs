using Haworks.Contracts.FeatureFlags;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.FeatureFlags.Api.Application;

public class FeatureFlagUpdatedConsumer : IConsumer<FeatureFlagUpdated>
{
    private readonly IFeatureFlagCache _cache;
    private readonly IServiceProvider _serviceProvider;

    public FeatureFlagUpdatedConsumer(IFeatureFlagCache cache, IServiceProvider serviceProvider)
    {
        _cache = cache;
        _serviceProvider = serviceProvider;
    }

    public async Task Consume(ConsumeContext<FeatureFlagUpdated> context)
    {
        // Re-hydrate full rules from DB on update to keep cache consistent
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Haworks.FeatureFlags.Api.Infrastructure.FeatureFlagsDbContext>();
        
        var flag = await db.FeatureFlags
            .Include(x => x.Rules)
            .FirstOrDefaultAsync(x => x.Name == context.Message.FlagName);

        if (flag != null)
        {
            _cache.Update(flag.Name, flag.IsEnabled, flag.Rules);
        }
    }
}
