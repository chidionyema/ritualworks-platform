using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.Orders.Application.Consumers;

namespace Haworks.Orders.Integration;

/// <summary>
/// WebApplicationFactory for orders-svc integration tests.
/// Same pattern as catalog/payments: Testcontainers postgres + in-memory
/// MassTransit harness with all 3 consumers wired so we can publish upstream
/// events into the harness and assert state + outbound publishes.
/// </summary>
public sealed class OrdersWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("orders")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__orders", ConnectionString);
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
                ["ConnectionStrings:orders"]   = ConnectionString,
                ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"]              = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<PaymentCompletedConsumer>();
                mt.AddConsumer<PaymentSessionFailedConsumer>();
                mt.AddConsumer<StockReservationFailedConsumer>();
            });
            services.AddDomainEventPublisher();

            // [Authorize]-decorated endpoints need an authentication scheme.
            // BuildingBlocks.Testing's TestAuthenticationHandler is a no-op
            // handler that auto-authenticates as a fixed test user.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Haworks.Orders.Infrastructure.OrderDbContext>();
        await db.Database.MigrateAsync();
    }
}
