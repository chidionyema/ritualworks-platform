using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.Behaviors;

namespace Haworks.BffWeb.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMediatR(cfg => {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        return services;
    }
}
