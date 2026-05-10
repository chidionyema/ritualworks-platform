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
/// Mirrors OrdersWebAppFactory / PaymentsWebAppFactory: SharedTestPostgres
/// + in-memory MassTransit harness with the L3 dispatch consumer registered
/// so we can publish NotificationCreatedEvent and observe DB state
/// transitions end-to-end.
///
/// **Single shared instance** across the whole test assembly via
/// <c>NotificationsIntegrationCollection</c> (one host build per
/// `dotnet test`, not per fixture). Tests that need different
/// IEmailProvider mocks call <c>factory.WithWebHostBuilder(...)</c>
/// per-test rather than subclassing the factory — see
/// .claude/rules/testing.md.
/// </summary>
public class NotificationsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

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
            // so PipelineTests can inject mocks per-test via
            // factory.WithWebHostBuilder(b => b.ConfigureTestServices(...)).
            // NotificationsApiTests doesn't dispatch through the gateway and
            // doesn't need any IEmailProvider — leaving none registered is fine
            // for those tests; the dispatch path simply never fires.
            var emailProviderDescriptors = services
                .Where(d => d.ServiceType == typeof(IEmailProvider))
                .ToList();
            foreach (var d in emailProviderDescriptors)
            {
                services.Remove(d);
            }

            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<NotificationRequestConsumer>();
            });

            services.AddDomainEventPublisher();

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
