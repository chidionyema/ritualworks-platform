using Microsoft.Extensions.DependencyInjection;
using Haworks.Contracts;
using Haworks.Contracts.Catalog;
using Haworks.Contracts.Identity;

namespace Haworks.Audit.Application.Extraction;

public static class ExtractorRegistry
{
    public static IServiceCollection AddExtractors(this IServiceCollection services)
    {
        var contractsAssembly = typeof(IDomainEvent).Assembly;
        var eventTypes = contractsAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDomainEvent).IsAssignableFrom(t));

        foreach (var eventType in eventTypes)
        {
            var interfaceType = typeof(IAuditExtractor<>).MakeGenericType(eventType);
            var implementationType = typeof(ReflectionAuditExtractor<>).MakeGenericType(eventType);
            
            services.AddSingleton(interfaceType, implementationType);
        }

        // Overrides (registered AFTER generic ones so they win in DI)
        services.AddSingleton<IAuditExtractor<StockReservationFailedEvent>, StockReservationFailedExtractor>();
        services.AddSingleton<IAuditExtractor<VaultRotationStageEvent>, VaultRotationStageExtractor>();
        services.AddSingleton<IAuditExtractor<ProductCacheInvalidatedEvent>, ProductCacheInvalidatedExtractor>();

        return services;
    }
}
