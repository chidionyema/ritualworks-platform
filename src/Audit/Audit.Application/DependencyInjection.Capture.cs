using Microsoft.Extensions.DependencyInjection;
using Haworks.Audit.Application.Capture;
using System.Linq;
using System;

namespace Haworks.Audit.Application;

/// <summary>
/// L1.B fills in this body — registers <see cref="IAuditWriter"/> (singleton,
/// COPY-batched) and <see cref="IAuditConsumerRegistry"/>.
/// </summary>
public static class AuditCaptureRegistration
{
    public static IServiceCollection AddAuditCapture(this IServiceCollection services)
    {
        // To avoid circular dependency between Application and Infrastructure
        // (since IAuditWriter is used by Application but implemented in Infrastructure),
        // we use reflection to find the implementation if it's available in the
        // loaded assemblies.
        var writerImplType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.Name == "AuditWriter" && typeof(IAuditWriter).IsAssignableFrom(t));

        if (writerImplType != null)
        {
            services.AddSingleton(typeof(IAuditWriter), writerImplType);
        }

        services.AddSingleton<IAuditConsumerRegistry, AuditConsumerRegistry>();
        return services;
    }
}
