using Amazon.S3;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Vault;
using Haworks.Content.Application.Interfaces;
using Haworks.Content.Application.Options;
using Haworks.Content.Domain.Interfaces;
using Haworks.Content.Infrastructure.BackgroundServices;
using Haworks.Content.Infrastructure.ExternalServices.Storage;
using Haworks.Content.Infrastructure.ExternalServices.Validation;
using Haworks.Content.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Haworks.Content.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("content")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No content database connection string. Expected 'ConnectionStrings:content' " +
                "(Aspire-injected) or 'ConnectionStrings:DefaultConnection'.");

        // Vault: dynamic Postgres creds via NpgsqlDataSource with PeriodicPasswordProvider.
        // Role haworks-content matches infra/vault/database/roles.json.
        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
            services.AddVaultNpgsqlDataSource(connectionString, "haworks-content");
        }

        services.AddDbContext<ContentDbContext>((sp, options) =>
        {
            if (vaultEnabled)
            {
                options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>());
            }
            else
            {
                options.UseNpgsql(connectionString);
            }
        });

        // Cross-cutting BuildingBlocks dependencies. TryAdd so we don't
        // collide with AddVaultIntegration's registrations when present.
        services.TryAddSingleton<IResiliencePolicyFactory, ResiliencePolicyFactory>();
        services.TryAddSingleton<ITelemetryService>(NullTelemetryService.Instance);

        services.AddScoped<IContentRepository, ContentContextRepository>();
        services.AddScoped<IFileSignatureValidator, FileSignatureValidator>();
        services.AddScoped<IVirusScanner, ClamAVScanner>();
        services.AddScoped<IUploadValidator, UploadValidator>();
        services.AddSingleton<IContentStorageService, S3ContentStorageService>();

        // S3 client (AWS SDK speaks S3 to anything S3-compatible). Bind
        // StorageOptions with [Required] DataAnnotations + ValidateOnStart
        // so a missing config key fails the host build, not the first request.
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var cfg = new AmazonS3Config
            {
                ServiceURL = opts.ServiceUrl,
                ForcePathStyle = opts.ForcePathStyle,
                AuthenticationRegion = opts.Region,
                // AWS SDK defaults presigned URLs to HTTPS regardless of what
                // ServiceURL declares. UseHttp = true (when ServiceURL is HTTP,
                // i.e. LocalStack in dev/test) forces presigned URLs to the
                // same scheme; without this clients PUT to https://… and
                // hit a TLS handshake against an HTTP-only emulator.
                UseHttp = opts.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            };
            return new AmazonS3Client(opts.AccessKey, opts.SecretKey, cfg);
        });

        services.TryAddSingleton(TimeProvider.System);

        // MassTransit + EF Core outbox: integration events (ContentAvailableEvent,
        // ContentDeletedEvent, ContentQuarantinedEvent) are persisted in the same
        // transaction as the entity state change, then delivered by the bus outbox.
        if (!env.IsEnvironment("Test"))
        {
            services.AddMassTransit(x =>
            {
                x.AddEntityFrameworkOutbox<ContentDbContext>(o =>
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

            services.AddDomainEventPublisher();
        }

        // Background sweeper for orphaned uploads. Skipped under Test so
        // integration fixtures don't fight the sweep loop.
        if (!env.IsEnvironment("Test"))
        {
            services.AddHostedService<UploadSweeperService>();
        }

        return services;
    }
}
