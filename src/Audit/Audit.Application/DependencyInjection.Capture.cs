using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Haworks.Audit.Application.Capture;
using System.Linq;
using System;
using System.Reflection;

namespace Haworks.Audit.Application;

/// <summary>
/// L1.B fills in this body — registers <see cref="IAuditWriter"/> (singleton,
/// COPY-batched) and <see cref="IAuditConsumerRegistry"/>.
/// </summary>
public static class AuditCaptureRegistration
{
    public static IServiceCollection AddAuditCapture(this IServiceCollection services, ILogger? _logger = null)
    {
        // Force-load Audit.Infrastructure so the assembly is present in the
        // AppDomain before we scan for AuditWriter.  Without this, the scan
        // can silently return null when the assembly hasn't been JIT-loaded yet
        // (e.g. during WebApplicationFactory startup in integration tests).
        EnsureInfrastructureAssemblyLoaded(_logger);

        // To avoid a hard project reference from Application → Infrastructure
        // (which would create a circular dependency), we locate AuditWriter via
        // reflection.  The force-load above makes this reliable.
        var writerImplType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => string.Equals(t.Name, "AuditWriter", StringComparison.Ordinal) && typeof(IAuditWriter).IsAssignableFrom(t));

        if (writerImplType != null)
        {
            services.AddSingleton(typeof(IAuditWriter), writerImplType);
        }

        services.AddSingleton<IAuditConsumerRegistry, AuditConsumerRegistry>();
        return services;
    }

    private static void EnsureInfrastructureAssemblyLoaded(ILogger? _logger = null)
    {
        // Walk every assembly already loaded and look for "Audit.Infrastructure"
        // by name.  If not found, attempt to load it by convention so the
        // reflection scan that follows will succeed.
        const string infraAssemblyName = "Audit.Infrastructure";
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetName().Name?.Contains(infraAssemblyName, StringComparison.OrdinalIgnoreCase) == true);

        if (!alreadyLoaded)
        {
            try
            {
                // The assembly ships alongside this one; Assembly.Load resolves
                // from the same probing path.
                Assembly.Load(new AssemblyName("Haworks." + infraAssemblyName));
            }
            catch (Exception ex)
            {
                // If the assembly genuinely isn't present (e.g. unit-test project
                // that only references Application), we tolerate the failure —
                // IAuditWriter simply won't be auto-registered and must be
                // provided by the host (e.g. ConfigureTestServices in tests).
                if (_logger != null)
                {
                    _logger.LogWarning(ex, "Failed to load Haworks.Audit.Infrastructure assembly");
                }
            }
        }
    }
}
