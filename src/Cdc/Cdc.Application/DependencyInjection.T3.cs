using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T3 — infrastructure and publications.
/// Currently purely data-layer configuration (Postgres SQL).
/// </summary>
public static class CdcT3Registration
{
    public static IServiceCollection AddCdcT3(this IServiceCollection services)
    {
        return services;
    }
}
