using Hangfire;
using Hangfire.PostgreSql;
using Haworks.Webhooks.Application.Interfaces;
using Haworks.Webhooks.Infrastructure.Persistence;
using Haworks.Webhooks.Infrastructure.Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Haworks.Webhooks.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddWebhooksInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("webhooks");

        services.AddDbContext<WebhooksDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "webhooks");
            });
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        services.AddScoped<IWebhooksDbContext>(sp => sp.GetRequiredService<WebhooksDbContext>());

        // Hangfire for durable retries
        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options => 
                options.UseNpgsqlConnection(connectionString), 
                new PostgreSqlStorageOptions 
                { 
                    SchemaName = "webhooks_jobs" 
                }));

        services.AddHangfireServer();

        services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
        
        services.AddHttpClient("WebhookValidator");
        services.AddHttpClient("WebhookDispatcher");

        return services;
    }
}
