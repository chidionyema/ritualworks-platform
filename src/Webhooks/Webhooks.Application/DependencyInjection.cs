using FluentValidation;
using Haworks.BuildingBlocks.Behaviors;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Webhooks.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddWebhooksApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => 
        {
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        
        return services;
    }
}
