using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Haworks.FeatureFlags.Api.Application;
using Haworks.FeatureFlags.Api.Infrastructure;

namespace Haworks.FeatureFlags.Integration;

public sealed class FeatureFlagsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("featureflags");
        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__featureflags", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__redis", "localhost:6379");
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
                ["ConnectionStrings:featureflags"] = ConnectionString,
                ["ConnectionStrings:redis"] = "localhost:6379",
                ["Vault:Enabled"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<FeatureFlagUpdatedConsumer>();
            });

            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FeatureFlagsDbContext>();
        await db.Database.MigrateAsync();
    }
}

[CollectionDefinition("FeatureFlags integration tests")]
public class SharedCollection : ICollectionFixture<FeatureFlagsWebAppFactory>
{
}
