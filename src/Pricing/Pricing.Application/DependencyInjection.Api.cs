using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Pricing.Application;

public static class PricingT3Registration
{
    public static IServiceCollection AddPricingT3(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(PricingT3Registration).Assembly));
        services.AddValidatorsFromAssembly(typeof(PricingT3Registration).Assembly);
        
        return services;
    }
}
