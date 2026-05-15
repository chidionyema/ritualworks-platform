using Haworks.Localization.Api.Application;
using Haworks.Localization.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using FluentValidation;
using Haworks.BuildingBlocks.Behaviors;

namespace Haworks.Localization.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddLocalizationService(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("localization")
            ?? throw new InvalidOperationException("ConnectionStrings:localization is required");

        services.AddDbContext<LocalizationDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        
        services.AddMediatR(cfg => {
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddScoped<ICdnService, MockCdnService>();

        if (!env.IsEnvironment("Test"))
        {
            services.AddMassTransit(mt =>
            {
                mt.SetKebabCaseEndpointNameFormatter();
                mt.UsingRabbitMq((context, cfg) =>
                {
                    var rabbitConn = configuration.GetConnectionString("rabbitmq") ?? throw new InvalidOperationException("RabbitMq:Username is required");
                    cfg.Host(new Uri(rabbitConn));
                    cfg.ConfigureEndpoints(context);
                });
            });
        }

        return services;
    }
}
