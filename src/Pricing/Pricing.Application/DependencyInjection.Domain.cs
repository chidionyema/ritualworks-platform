using Microsoft.Extensions.DependencyInjection;
using Haworks.Pricing.Domain.Aggregates;

namespace Haworks.Pricing.Application;

public static class DependencyInjectionDomain
{
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        return services;
    }

    // This prevents MSBuild from trimming the transitive dependency to Haworks.Pricing.Domain
    public static Promotion? PreventReferenceTrimming(Promotion? p) => p;
}
