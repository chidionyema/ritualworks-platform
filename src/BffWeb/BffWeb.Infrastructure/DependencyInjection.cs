using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Haworks.BuildingBlocks.Messaging;

namespace Haworks.BffWeb.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        // MassTransit + the PaymentSessionCreatedConsumer are wired by the Api
        // project's Program.cs (it owns the consumer type, which lives under
        // BffWeb.Api/SignalR/). Calling AddMassTransit in two places throws
        // ConfigurationException — see ADR-0010 footnote in CHANGELOG. The
        // domain event publisher still belongs in Infrastructure since it's
        // pure plumbing.
        if (env.IsEnvironment("Test"))
        {
            return services;
        }

        services.AddDomainEventPublisher();

        return services;
    }
}
