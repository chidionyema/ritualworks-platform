using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Haworks.Search.Application.Consumers;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Haworks.BuildingBlocks.Testing.Authentication;
using WireMock.Server;
using Xunit;

namespace Haworks.Search.Integration;

/// <summary>
/// Test fixture for Search.Integration. Spins an Elasticsearch container +
/// a WireMock server (the catalog stub), sets configuration env vars
/// before the host builds (Program.cs reads <c>builder.Configuration</c>
/// before <c>ConfigureAppConfiguration</c> fires), and registers an
/// in-memory MassTransit harness with the indexer's consumers.
///
/// Mirrors the pattern stabilised in PaymentsWebAppFactory.
/// </summary>
public sealed class SearchWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly IContainer _es = new ContainerBuilder()
        .WithImage("docker.elastic.co/elasticsearch/elasticsearch:8.17.0")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("xpack.security.enabled", "false")
        .WithEnvironment("ES_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithPortBinding(9200, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/").ForPort(9200)))
        .WithReuse(true)
        .Build();

    public WireMockServer Catalog { get; } = WireMockServer.Start();

    public string EsUrl => $"http://{_es.Hostname}:{_es.GetMappedPublicPort(9200)}";

    public async Task InitializeAsync()
    {
        await _es.StartAsync();
        JwtTestDefaults.SetTestEnvironmentVariables();

        // Program.cs reads builder.Configuration before WAF's ConfigureAppConfiguration
        // hook runs, so configuration must be present as env vars by then.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("Elasticsearch__Url", EsUrl);
        Environment.SetEnvironmentVariable("Elasticsearch__IndexName", $"products_test_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Catalog__BaseAddress", Catalog.Url);
    }

    public new Task DisposeAsync()
    {
        Catalog.Stop();
        Catalog.Dispose();
        // Reused Elasticsearch container outlives the fixture intentionally.
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
                ["Elasticsearch:IndexName"] = Environment.GetEnvironmentVariable("Elasticsearch__IndexName"),
                ["Catalog:BaseAddress"] = Catalog.Url,
            });
        });

        // In-memory MassTransit harness with our consumers — production's
        // AddMassTransit(...) is skipped under Test (see DependencyInjection.cs).
        builder.ConfigureTestServices(services =>
        {
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<ProductCacheInvalidatedConsumer>();
                mt.AddConsumer<CategoryUpdatedConsumer>();
            });

            // [Authorize]-decorated endpoints need an authentication scheme.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }
}
