using Microsoft.Extensions.DependencyInjection;
using Haworks.Webhooks.Application.Subscriptions;

namespace Haworks.Webhooks.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddWebhooksApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => 
        {
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            cfg.AddOpenBehavior(typeof(Haworks.BuildingBlocks.Middleware.ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        
        return services;
    }
}
