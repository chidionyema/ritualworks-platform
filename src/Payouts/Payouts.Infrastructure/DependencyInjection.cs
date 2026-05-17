using Haworks.BuildingBlocks.Messaging;
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
using System;

namespace Haworks.Payouts.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("payouts");
        services.AddDbContext<PayoutsDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });
        services.AddScoped<IPayoutsDbContext>(provider => provider.GetRequiredService<PayoutsDbContext>());
        services.AddScoped<IPayoutGateway, StripePayoutGateway>();
        services.AddScoped<Haworks.Payouts.Application.Ledger.Services.ILedgerService, Haworks.Payouts.Application.Ledger.Services.LedgerService>();
        services.AddScoped<Haworks.Payouts.Application.Disbursements.Services.IDisbursementService, Haworks.Payouts.Application.Disbursements.Services.DisbursementService>();
        if (!string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Test", StringComparison.Ordinal))
        {
            services.AddMassTransit(x => {
                x.SetKebabCaseEndpointNameFormatter();
                x.AddConsumer<PaymentCompletedConsumer, Messaging.PayoutsConsumerDefinition<PaymentCompletedConsumer>>();
                x.AddConsumer<RefundIssuedConsumer, Messaging.PayoutsConsumerDefinition<RefundIssuedConsumer>>();
                x.AddEntityFrameworkOutbox<PayoutsDbContext>(o =>
                {
                    o.UsePostgres();
                    o.UseBusOutbox();
                    o.QueryDelay = TimeSpan.FromSeconds(1);
                    o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
                });
                x.UsingRabbitMq((context, cfg) => {
                    var rabbitConn = configuration.GetConnectionString("rabbitmq")
                        ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is required");
                    cfg.Host(new Uri(rabbitConn));
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureStandardRabbitMq(context);
                });
            });
        }
        if (!string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Test", StringComparison.Ordinal))
        {
            services.AddHangfire(config => config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180).UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings().UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
            services.AddHangfireServer();
        }
        return services;
    }
}
