using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.Privacy.Application.Requests.Sagas;
using Haworks.Privacy.Infrastructure.Persistence;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Haworks.Privacy.Integration;

/// <summary>
/// Test harness for the PrivacyRequestStateMachine saga. Uses the shared
/// Testcontainers Postgres with MassTransit in-memory test harness so the
/// EF saga repository, outbox, and xmin concurrency are exercised exactly
/// as in production. The in-memory transport replaces RabbitMQ.
/// </summary>
public sealed class PrivacyWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("privacy");
        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__privacy", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", "amqp://guest:guest@localhost:5672/");
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
                ["ConnectionStrings:privacy"]  = ConnectionString,
                ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"]              = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // AddEntityFrameworkOutbox is required because the saga uses PublishAsync
            // within its state transitions. Without the outbox, the publish faults.
            // Note: UseBusOutbox is NOT used here to avoid intercepting test helper publishes.
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddEntityFrameworkOutbox<PrivacyDbContext>(o =>
                {
                    o.UsePostgres();
                });

                mt.AddSagaStateMachine<PrivacyRequestStateMachine, PrivacyRequestState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ExistingDbContext<PrivacyDbContext>();
                        r.UsePostgres();
                    });
            });

            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrivacyDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS privacy;");
            await db.Database.EnsureCreatedAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
