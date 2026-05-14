using Haworks.Search.Application.Consumers;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using WireMock.Server;
using Xunit;

namespace Haworks.Search.Integration;

public sealed class SearchWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public WireMockServer Catalog { get; } = WireMockServer.Start();

    public string EsUrl { get; private set; } = string.Empty;
    public string IndexName { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var (url, indexName) = await SharedTestElasticsearch.GetConnectionAsync("search");
        EsUrl = url;
        IndexName = indexName;

        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("Elasticsearch__Url", EsUrl);
        Environment.SetEnvironmentVariable("Elasticsearch__IndexName", IndexName);
        Environment.SetEnvironmentVariable("Catalog__BaseAddress", Catalog.Url);
    }

    public new Task DisposeAsync()
    {
        Catalog.Stop();
        Catalog.Dispose();
        return Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Elasticsearch:Url"] = EsUrl,
                ["Elasticsearch:IndexName"] = IndexName,
                ["Catalog:BaseAddress"] = Catalog.Url,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<ProductCacheInvalidatedConsumer>();
                mt.AddConsumer<CategoryUpdatedConsumer>();
            });

            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }
}
