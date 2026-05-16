using Haworks.Contracts.FeatureFlags;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.FeatureFlags.Api.Application;

public class FeatureFlagUpdatedConsumer : IConsumer<FeatureFlagUpdated>
{
    private readonly IFeatureFlagCache _cache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FeatureFlagUpdatedConsumer> _logger;

    public FeatureFlagUpdatedConsumer(
        IFeatureFlagCache cache,
        IServiceProvider serviceProvider,
        ILogger<FeatureFlagUpdatedConsumer> logger)
    {
        _cache = cache;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<FeatureFlagUpdated> context)
    {
        // Re-hydrate full rules from DB on update to keep cache consistent
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Haworks.FeatureFlags.Api.Infrastructure.FeatureFlagsDbContext>();

        var flag = await db.FeatureFlags
            .Include(x => x.Rules)
            .FirstOrDefaultAsync(x => x.Name == context.Message.FlagName);

        if (flag == null)
        {
            _logger.LogWarning(
                "FeatureFlagUpdated received for unknown flag '{FlagName}'. Cache not updated.",
                context.Message.FlagName);
            return;
        }

        _cache.Update(flag.Name, flag.IsEnabled, flag.Rules);
        _logger.LogInformation(
            "Feature flag '{FlagName}' cache refreshed. Enabled={IsEnabled}",
            flag.Name, flag.IsEnabled);
    }
}
