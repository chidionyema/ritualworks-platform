using Haworks.Privacy.Application.Common.Interfaces;
using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Privacy.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("privacy");

        services.AddDbContext<PrivacyDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IPrivacyDbContext>(provider => provider.GetRequiredService<PrivacyDbContext>());

        services.AddMassTransit(x =>
        {
            x.AddSagaStateMachine<PrivacyRequestStateMachine, PrivacyRequestState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<PrivacyDbContext>();
                    r.UsePostgres();
                });

            x.AddEntityFrameworkOutbox<PrivacyDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbitMqConfig = configuration.GetSection("RabbitMq");
                cfg.Host(rabbitMqConfig["Host"], "/", h =>
                {
                    h.Username(rabbitMqConfig["Username"] ?? "guest");
                    h.Password(rabbitMqConfig["Password"] ?? "guest");
                });
                
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
