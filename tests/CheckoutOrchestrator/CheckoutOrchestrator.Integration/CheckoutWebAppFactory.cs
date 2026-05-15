using Microsoft.AspNetCore.TestHost;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.CheckoutOrchestrator.Application.Sagas;
using Haworks.CheckoutOrchestrator.Domain;
using Haworks.CheckoutOrchestrator.Infrastructure;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;

namespace Haworks.CheckoutOrchestrator.Integration;

/// <summary>
/// Test harness for the saga. Wires the InMemory MassTransit transport
/// PLUS the EF saga repository against a Testcontainers postgres so the
/// saga's state-machine transitions persist exactly as they would in
/// production (xmin concurrency, OutboxMessage table, the lot). The
/// in-memory transport just substitutes for RabbitMQ — the saga, EF
/// repo, outbox, and DbContext are the same wiring as production.
/// </summary>
public sealed class CheckoutWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("checkout");
        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__checkout", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", "amqp://guest:guest@localhost:5672/");
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
                ["ConnectionStrings:checkout"]  = ConnectionString,
                ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"]              = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddSagaStateMachine<CheckoutSaga, CheckoutSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ExistingDbContext<CheckoutDbContext>();
                        r.UsePostgres();
                    });
            });

            // [Authorize]-decorated endpoints need an authentication scheme.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        await db.Database.MigrateAsync();
    }
}
