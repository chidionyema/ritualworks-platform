using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T4 — consumer adaptation.
/// Handles wiring of CDC consumers in downstream services.
/// </summary>
public static class CdcT4Registration
{
    public static IServiceCollection AddCdcT4(this IServiceCollection services)
    {
        return services;
    }
}
