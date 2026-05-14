using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Haworks.Merchant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("merchant");

        services.AddDbContext<MerchantDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IMerchantDbContext>(provider => provider.GetRequiredService<MerchantDbContext>());

        services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<MerchantDbContext>(o =>
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
