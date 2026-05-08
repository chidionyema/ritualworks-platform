using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Haworks.Search.Integration;

/// <summary>
/// Test fixture for Search.Integration. Spins a Meilisearch container,
/// sets <c>Meilisearch__Url</c> + <c>Meilisearch__MasterKey</c> env vars
/// before the host builds (Program.cs reads <c>builder.Configuration</c>
/// before <c>ConfigureAppConfiguration</c> fires), and exposes Services
/// for tests to resolve <c>ISearchIndex</c>.
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
        .Build();

    public string MeiliUrl => $"http://{_meili.Hostname}:{_meili.GetMappedPublicPort(7700)}";

    public async Task InitializeAsync()
    {
        await _meili.StartAsync();

        // Program.cs reads builder.Configuration before WAF's ConfigureAppConfiguration
        // hook runs, so configuration must be present as env vars by then.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("Meilisearch__Url", MeiliUrl);
        Environment.SetEnvironmentVariable("Meilisearch__MasterKey", TestMasterKey);
        Environment.SetEnvironmentVariable("Meilisearch__IndexName", $"products_test_{Guid.NewGuid():N}");
    }

    public new async Task DisposeAsync()
    {
        await _meili.DisposeAsync();
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
            });
        });
    }
}
