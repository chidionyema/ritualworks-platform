using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Haworks.Merchant.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("merchant");

        services.AddDbContext<MerchantDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IMerchantDbContext>(provider => provider.GetRequiredService<MerchantDbContext>());

        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Test")
        {
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
                        h.Username(rabbitMqConfig["Username"] ?? throw new InvalidOperationException("RabbitMq:Username is required"));
                        h.Password(rabbitMqConfig["Password"] ?? throw new InvalidOperationException("RabbitMq:Password is required"));
                    });

                    cfg.ConfigureStandardRabbitMq(context);
                });
            });
        }

        return services;
    }
}
