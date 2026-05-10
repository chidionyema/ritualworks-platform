using Haworks.Audit.Application.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Audit.Application;

public static class AuditQueriesRegistration
{
    public static IServiceCollection AddAuditQueries(this IServiceCollection services)
    {
        // Register IAuditQueryService implementation via reflection to avoid
        // circular dependency between Application and Infrastructure.
        var infraAssembly = "Haworks.Audit.Infrastructure";
        var implementationType = Type.GetType($"Haworks.Audit.Infrastructure.Queries.AuditQueryService, {infraAssembly}");
        
        if (implementationType != null)
        {
            services.AddScoped(typeof(IAuditQueryService), implementationType);
        }

        return services;
    }
}
