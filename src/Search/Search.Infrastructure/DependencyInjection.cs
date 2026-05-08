using Haworks.Search.Application.Interfaces;
using Haworks.Search.Infrastructure.Meilisearch;
using Haworks.Search.Infrastructure.Options;
using Meilisearch;
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
        services.AddOptions<MeilisearchOptions>()
            .Bind(configuration.GetSection(MeilisearchOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<MeilisearchClient>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<MeilisearchOptions>>().Value;
            return new MeilisearchClient(opt.Url, opt.MasterKey);
        });

        services.AddScoped<ISearchIndex, MeilisearchIndex>();

        return services;
    }
}
