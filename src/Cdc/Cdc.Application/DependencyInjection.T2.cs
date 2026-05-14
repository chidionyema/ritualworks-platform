using Haworks.Cdc.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T2 — implementation of the Admin API and Relay Management.
/// </summary>
public static class CdcT2Registration
{
    public static IServiceCollection AddCdcT2(this IServiceCollection services)
    {
        services.AddSingleton<CdcRelayManager>();
        services.AddHostedService<CdcRelayBackgroundService>();
        
        return services;
    }
}
