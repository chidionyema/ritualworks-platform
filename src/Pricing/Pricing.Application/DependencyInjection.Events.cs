using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Pricing.Application;

public static class PricingEventsRegistration
{
    public static IServiceCollection AddPricingEvents(this IServiceCollection services)
    {
        // TODO(pricing-T1/T2): Call this from orchestrator or DependencyInjection.T4.cs
        // MassTransit consumers in this assembly are auto-discovered during x.AddConsumers
        return services;
    }
}
