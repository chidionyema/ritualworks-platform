using Microsoft.AspNetCore.TestHost;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;

namespace Haworks.Analytics.Integration;

public sealed class AnalyticsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("analytics");
        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__analytics", ConnectionString);
        // Fake connection string for Kafka to prevent potential issues during AddKafkaProducer
        Environment.SetEnvironmentVariable("ConnectionStrings__kafka", "localhost:9092");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:analytics"] = ConnectionString,
                ["ConnectionStrings:kafka"] = "localhost:9092",
                ["Vault:Enabled"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace MassTransit transport with test harness as requested
            services.AddMassTransitTestHarness();

            // [Authorize]-decorated endpoints need an authentication scheme.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }
}
