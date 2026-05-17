using FluentValidation;
using Haworks.Pricing.Application.Interfaces;
using Haworks.Pricing.Application.Options;
using Haworks.Pricing.Application.Services;
using Haworks.Pricing.Infrastructure.Adapters;
using Haworks.Pricing.Infrastructure.Http;
using Haworks.Pricing.Infrastructure.Persistence;
using Haworks.Pricing.Infrastructure.Repositories;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Refit;

namespace Haworks.Pricing.Infrastructure;

/// <summary>
/// DI registration for the Pricing service.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        var connectionString = configuration.GetConnectionString("pricing")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:pricing is missing.");

        services.AddDbContext<PricingDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "pricing"));
        });

        // Repositories
        services.AddScoped<IPriceRuleRepository, PriceRuleRepository>();
        services.AddScoped<IPromotionCodeRepository, PromotionCodeRepository>();
        services.AddScoped<ITaxRateRepository, TaxRateRepository>();
        services.AddScoped<ICalculationLogRepository, CalculationLogRepository>();

        // Tax calculator — use config-based adapter for v1
        services.Configure<TaxOptions>(configuration.GetSection(TaxOptions.SectionName));
        var taxProvider = configuration.GetValue("Pricing:TaxProvider", "ConfigurableRate");
        if (string.Equals(taxProvider, "RateTable", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<ITaxCalculator, RateTableTaxCalculator>();
        }
        else
        {
            services.AddSingleton<ITaxCalculator, ConfigurableRateTaxAdapter>();
        }

        // Calculation engine (stateless, singleton)
        services.AddSingleton<PriceCalculationEngine>();

        // Memory cache for catalog price lookups (60s)
        services.AddMemoryCache();

        // Catalog HTTP client (Refit)
        var catalogBaseUrl = configuration["Catalog:BaseUrl"]
            ?? configuration.GetConnectionString("catalog-api")
            ?? throw new InvalidOperationException(
                "Catalog:BaseUrl or ConnectionStrings:catalog-api must be configured.");

        services.AddRefitClient<IRefitCatalogClient>()
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri(catalogBaseUrl);
                c.Timeout = TimeSpan.FromSeconds(5);
            });

        services.AddScoped<ICatalogPricingClient, CatalogPricingHttpClient>();

        // MassTransit
        if (!env.IsEnvironment("Test"))
        {
            services.AddMassTransit(mt =>
            {
                mt.SetKebabCaseEndpointNameFormatter();
                mt.AddDelayedMessageScheduler();

                mt.AddConsumers(
                    typeof(DependencyInjection).Assembly,
                    typeof(Haworks.Pricing.Application.Services.PriceCalculationEngine).Assembly);

                mt.AddEntityFrameworkOutbox<Persistence.PricingDbContext>(o =>
                {
                    o.UsePostgres().UseBusOutbox();
                });

                mt.UsingRabbitMq((context, cfg) =>
                {
                    var rabbitHost = configuration.GetConnectionString("rabbitmq")
                        ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is required");
                    cfg.Host(new Uri(rabbitHost));
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            });
        }

        return services;
    }

    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssemblyContaining<PriceCalculationEngine>());

        services.AddValidatorsFromAssemblyContaining<PriceCalculationEngine>();

        return services;
    }
}
