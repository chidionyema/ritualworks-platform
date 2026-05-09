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
/// Test fixture for Search.Integration. Spins a Meilisearch container +
/// a WireMock server (the catalog stub), sets configuration env vars
/// before the host builds (Program.cs reads <c>builder.Configuration</c>
/// before <c>ConfigureAppConfiguration</c> fires), and registers an
/// in-memory MassTransit harness with the indexer's consumers.
///
/// Mirrors the pattern stabilised in PaymentsWebAppFactory.
/// </summary>
public sealed class SearchWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestMasterKey = "test_master_key_at_least_16_chars";

    private readonly IContainer _meili = new ContainerBuilder()
        .WithImage("getmeili/meilisearch:v1.10")
        .WithEnvironment("MEILI_MASTER_KEY", TestMasterKey)
        .WithEnvironment("MEILI_NO_ANALYTICS", "true")
        .WithPortBinding(7700, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(7700)))
        .WithReuse(true)
        .Build();

    public WireMockServer Catalog { get; } = WireMockServer.Start();

    public string MeiliUrl => $"http://{_meili.Hostname}:{_meili.GetMappedPublicPort(7700)}";

    public async Task InitializeAsync()
    {
        await _meili.StartAsync();
        JwtTestDefaults.SetTestEnvironmentVariables();

        // Program.cs reads builder.Configuration before WAF's ConfigureAppConfiguration
        // hook runs, so configuration must be present as env vars by then.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("Meilisearch__Url", MeiliUrl);
        Environment.SetEnvironmentVariable("Meilisearch__MasterKey", TestMasterKey);
        Environment.SetEnvironmentVariable("Meilisearch__IndexName", $"products_test_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Catalog__BaseAddress", Catalog.Url);
    }

    public new Task DisposeAsync()
    {
        Catalog.Stop();
        Catalog.Dispose();
        // Reused Meilisearch container outlives the fixture intentionally.
        return Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Meilisearch:Url"] = MeiliUrl,
                ["Meilisearch:MasterKey"] = TestMasterKey,
                ["Meilisearch:IndexName"] = Environment.GetEnvironmentVariable("Meilisearch__IndexName"),
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
