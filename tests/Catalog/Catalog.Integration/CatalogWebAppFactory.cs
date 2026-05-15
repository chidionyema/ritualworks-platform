using Microsoft.AspNetCore.TestHost;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;

namespace Haworks.Catalog.Integration;

/// <summary>
/// WebApplicationFactory for catalog-svc integration tests.
///
/// • Spins up its own postgres via Testcontainers — no shared Aspire.
/// • Sets ASPNETCORE_ENVIRONMENT=Test to skip Program.cs's auto-migrate
///   block (we apply migrations explicitly in <see cref="EnsureSchemaAsync"/>).
/// • Replaces the production RabbitMQ MassTransit transport with the
///   in-memory transport + TestHarness so we can assert
///   StockReservedEvent publishes deterministically without needing a
///   RabbitMQ container.
/// • Vault is disabled — catalog-svc has no Vault-backed secrets in Phase 2.
/// </summary>
public sealed class CatalogWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("catalog");
        JwtTestDefaults.SetTestEnvironmentVariables();

        // Env vars must be set BEFORE WebApplicationFactory builds the host.
        // Top-level Program.cs runs to construct the WebApplicationBuilder
        // before WAF's ConfigureAppConfiguration hook fires; env vars are
        // picked up by the default ConfigurationBuilder.AddEnvironmentVariables().
        // Set env BEFORE Program.cs runs so AddInfrastructure can read it
        // and skip the production MassTransit/RabbitMQ wiring before we
        // graft the in-memory test harness in ConfigureServices.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__catalog", ConnectionString);
        // Prevent the production RabbitMQ wiring from blowing up at AddMassTransit
        // time — we'll replace the transport entirely below, but the
        // GetConnectionString call still needs a string. Any URI parses fine.
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq",
            "amqp://guest:guest@localhost:5672/");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Shared Postgres container outlives the fixture intentionally.
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:catalog"]  = ConnectionString,
                ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Production AddMassTransit + AddDomainEventPublisher were skipped
            // by AddInfrastructure when ASPNETCORE_ENVIRONMENT=Test (see
            // Catalog.Infrastructure.DependencyInjection). Wire the in-memory
            // test harness + the publisher here so the handler can publish
            // and tests can assert via ITestHarness.Published.
            services.AddMassTransitTestHarness(mt =>
            {
                // No additional consumers — catalog-svc is a producer for
                // StockReservedEvent. Tests inspect the harness Published
                // collection to assert events landed.
            });
            services.AddDomainEventPublisher();

            // [Authorize]-decorated endpoints need an authentication scheme.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    /// <summary>Apply EF migrations once per fixture lifetime.</summary>
    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Haworks.Catalog.Infrastructure.CatalogDbContext>();
        await db.Database.MigrateAsync();
    }
}
