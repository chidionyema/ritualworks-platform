using Haworks.BuildingBlocks.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.BuildingBlocks.Extensions;

public static class BrandExtensions
{
    /// <summary>
    /// Registers the platform-wide Brand configuration.
    /// Supports dynamic reloading via IOptionsMonitor.
    /// </summary>
    public static IServiceCollection AddBrandConfiguration(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        services.Configure<BrandOptions>(
            configuration.GetSection(BrandOptions.SectionName));

        return services;
    }
}
