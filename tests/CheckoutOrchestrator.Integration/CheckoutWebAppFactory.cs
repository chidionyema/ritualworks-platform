using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using Haworks.CheckoutOrchestrator.Application.Sagas;
using Haworks.CheckoutOrchestrator.Domain;
using Haworks.CheckoutOrchestrator.Infrastructure;

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
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("checkout")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__checkout", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", "amqp://guest:guest@localhost:5672/");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
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

        builder.ConfigureServices(services =>
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
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
        await db.Database.MigrateAsync();
    }
}
