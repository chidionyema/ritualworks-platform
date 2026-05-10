using Haworks.Audit.Application.Extraction;
using Haworks.Audit.Application.Redaction;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Audit.Application;

public static class AuditExtractorsRegistration
{
    public static IServiceCollection AddAuditExtractors(this IServiceCollection services)
    {
        services.AddExtractors();
        services.AddSingleton<ISecretRedactor, SecretRedactor>();
        
        return services;
    }
}
