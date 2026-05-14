using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T5 — cache invalidation and E2E validation.
/// Stub for DI consistency across tracks.
/// </summary>
public static class CdcT5Registration
{
    public static IServiceCollection AddCdcT5(this IServiceCollection services)
    {
        return services;
    }
}
