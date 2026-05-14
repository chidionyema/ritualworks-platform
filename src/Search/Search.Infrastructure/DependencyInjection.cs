using Elastic.Clients.Elasticsearch;
using Haworks.BuildingBlocks.Resilience;
using Haworks.Search.Application.Catalog;
using Haworks.Search.Application.Consumers;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Infrastructure.Catalog;
using Haworks.Search.Infrastructure.Elasticsearch;
using Haworks.Search.Infrastructure.Options;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Haworks.Search.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        services.AddOptions<ElasticsearchOptions>()
            .Bind(configuration.GetSection(ElasticsearchOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ElasticsearchClient>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<ElasticsearchOptions>>().Value;
            var settings = new ElasticsearchClientSettings(new Uri(opt.Url));
            return new ElasticsearchClient(settings);
        });

        services.AddScoped<ISearchIndex, ElasticsearchIndex>();

        // Catalog HTTP client. The resilience policy is constructed inside
        // CatalogProductsApiClient and wraps each call — matching the
        // Stripe/PayPal pattern — so AddPolicyHandler isn't needed here.
        services.AddSingleton<IResiliencePolicyFactory, ResiliencePolicyFactory>();
        services.AddHttpClient<ICatalogProductsApi, CatalogProductsApiClient>(c =>
        {
            c.BaseAddress = new Uri(configuration["Catalog:BaseAddress"]
                ?? "http://ritualworks-catalog.flycast:8080");
            c.Timeout = TimeSpan.FromSeconds(5);
        });

        // MassTransit + RabbitMQ. Skipped under Test — SearchWebAppFactory
        // wires AddMassTransitTestHarness with the same consumers.
        if (!env.IsEnvironment("Test"))
        {
            services.AddMassTransit(mt =>
            {
                mt.SetKebabCaseEndpointNameFormatter();
                mt.AddConsumer<ProductCacheInvalidatedConsumer>();
                mt.AddConsumer<CategoryUpdatedConsumer>();

                mt.UsingRabbitMq((ctx, cfg) =>
                {
                    var rabbit = configuration.GetConnectionString("rabbitmq")
                        ?? throw new InvalidOperationException("ConnectionStrings:rabbitmq is missing");
                    cfg.Host(new Uri(rabbit));
                    cfg.ConfigureEndpoints(ctx);
                });
            });
        }

        return services;
    }
}
