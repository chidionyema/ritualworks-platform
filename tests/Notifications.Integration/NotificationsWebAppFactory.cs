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
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Application.Consumers;
using Haworks.Notifications.Infrastructure.Persistence;

namespace Haworks.Notifications.Integration;

/// <summary>
/// WebApplicationFactory for notifications-svc integration tests.
/// Mirrors OrdersWebAppFactory / PaymentsWebAppFactory: Testcontainers
/// Postgres + in-memory MassTransit harness with the L3 dispatch consumer
/// registered so we can publish NotificationCreatedEvent and observe DB
/// state transitions end-to-end.
///
/// IEmailProvider implementations are stripped from DI and a single mock
/// is wired in by default; PipelineTests override per-test to install
/// failover scenarios.
/// </summary>
public class NotificationsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Hook for individual test classes to override the default IEmailProvider
    /// registration before the host is built. PipelineTests sets this to
    /// install primary+secondary mocks for the failover path.
    /// </summary>
    public Action<IServiceCollection>? ConfigureEmailProviders { get; set; }

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("notifications");
        JwtTestDefaults.SetTestEnvironmentVariables();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__Notifications", ConnectionString);
        // Notifications.Infrastructure resolves the rabbit conn via GetConnectionString("RabbitMQ").
        // Set a dummy value so the AddMassTransit call in the prod DI doesn't
        // throw when we replace it with the test harness below.
        Environment.SetEnvironmentVariable("ConnectionStrings__RabbitMQ", "amqp://guest:guest@localhost:5672/");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");

        // SesOptions has [Required] + ValidateOnStart; provide test-grade
        // values so the host can boot. The provider itself is removed and
        // replaced with a mock in ConfigureWebHost below, so these values
        // are never actually used at runtime.
        Environment.SetEnvironmentVariable("Notifications__Providers__Ses__AccessKey", "test-access-key");
        Environment.SetEnvironmentVariable("Notifications__Providers__Ses__SecretKey", "test-secret-key");
        Environment.SetEnvironmentVariable("Notifications__Providers__Ses__Region", "us-east-1");
        Environment.SetEnvironmentVariable("Notifications__Providers__Ses__FromAddress", "noreply@test.invalid");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // Shared Postgres container outlives the fixture intentionally.
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Notifications"] = ConnectionString,
                ["ConnectionStrings:RabbitMQ"] = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"] = "false",
                ["Notifications:Providers:Ses:AccessKey"] = "test-access-key",
                ["Notifications:Providers:Ses:SecretKey"] = "test-secret-key",
                ["Notifications:Providers:Ses:Region"] = "us-east-1",
                ["Notifications:Providers:Ses:FromAddress"] = "noreply@test.invalid",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Strip every IEmailProvider registered by Infrastructure (SES, etc.)
            // so the channel gateway only sees the test mocks.
            var emailProviderDescriptors = services
                .Where(d => d.ServiceType == typeof(IEmailProvider))
                .ToList();
            foreach (var d in emailProviderDescriptors)
            {
                services.Remove(d);
            }

            // Replace MassTransit with the in-memory test harness. AddMassTransit
            // calls are additive — the AddMassTransitTestHarness call REGISTERS
            // a fresh harness-based bus that wins because the Infrastructure-side
            // RabbitMQ host registration is gated behind the harness defaults.
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<NotificationRequestConsumer>();
            });

            services.AddDomainEventPublisher();

            // Default email provider mock — replaced per-test by PipelineTests
            // for the failover scenario.
            ConfigureEmailProviders?.Invoke(services);

            // [Authorize]-decorated endpoints need an auth scheme. Tests use
            // the no-op TestAuthenticationHandler.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    /// <summary>
    /// Spec asks for MigrateAsync, but Notifications.Infrastructure has no
    /// EF migrations checked in (only MigrationsAssembly is wired). Use
    /// EnsureCreated against a fresh per-fixture database so the schema lands
    /// in one shot — same pattern as PaymentsWebAppFactory.EnsureSchemaAsync.
    /// </summary>
    public async Task EnsureSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS notifications;");
            await db.Database.EnsureCreatedAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
