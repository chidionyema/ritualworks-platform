using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Infrastructure.Gateways;
using Haworks.Payouts.Infrastructure.Messaging.Consumers;
using Haworks.Payouts.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using Hangfire.PostgreSql;

namespace Haworks.Payouts.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("payouts");
        services.AddDbContext<PayoutsDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IPayoutsDbContext>(provider => provider.GetRequiredService<PayoutsDbContext>());
        services.AddScoped<IPayoutGateway, StripePayoutGateway>();
        services.AddMassTransit(x => {
            x.AddConsumer<PaymentCompletedConsumer>();
            x.UsingRabbitMq((context, cfg) => {
                var rabbitMqConfig = configuration.GetSection("RabbitMq");
                cfg.Host(rabbitMqConfig["Host"], "/", h => {
                    h.Username(rabbitMqConfig["Username"] ?? "guest");
                    h.Password(rabbitMqConfig["Password"] ?? "guest");
                });
                cfg.ReceiveEndpoint("payouts-payment-completed", e => e.ConfigureConsumer<PaymentCompletedConsumer>(context));
            });
        });
        services.AddHangfire(config => config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings().UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
        services.AddHangfireServer();
        return services;
    }
}
