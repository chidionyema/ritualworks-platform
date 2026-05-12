using Haworks.Cdc.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System;

namespace Haworks.Cdc.Application;

/// <summary>
/// Track T1 — implementation of the core CDC relay engine.
/// </summary>
public static class CdcT1Registration
{
    public static IServiceCollection AddCdcT1(this IServiceCollection services)
    {
        // To avoid circular dependency between Application and Infrastructure
        // (since ICdcRelay is used by Application but implemented in Infrastructure),
        // we use reflection to find the implementation if it's available in the
        // loaded assemblies.
        var relayImplType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.Name == "PostgresLogicalReplicationSubscriber" && typeof(ICdcRelay).IsAssignableFrom(t));

        if (relayImplType != null)
        {
            services.AddScoped(typeof(ICdcRelay), relayImplType);
        }

        var decoderImplType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.Name == "PgOutputDecoder");

        if (decoderImplType != null)
        {
            services.AddTransient(decoderImplType);
        }

        return services;
    }
}
