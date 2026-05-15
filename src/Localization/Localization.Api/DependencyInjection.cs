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
            ?? "Host=localhost;Database=localization;Username=postgres;Password=postgres";

        services.AddDbContext<LocalizationDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        
        services.AddMediatR(cfg => {
            cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
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
                    var rabbitConn = configuration.GetConnectionString("rabbitmq") ?? "amqp://guest:guest@localhost:5672";
                    cfg.Host(new Uri(rabbitConn));
                    cfg.ConfigureEndpoints(context);
                });
            });
        }

        return services;
    }
}
