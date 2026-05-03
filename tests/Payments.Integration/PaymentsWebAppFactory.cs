using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Payments.Application.Consumers;

namespace Haworks.Payments.Integration;

/// <summary>
/// WebApplicationFactory for payments-svc integration tests.
///
/// • Spins up its own postgres via Testcontainers — no shared Aspire.
/// • Sets ASPNETCORE_ENVIRONMENT=Test BEFORE Program.cs runs so
///   AddInfrastructure short-circuits the production MassTransit/RabbitMQ
///   wiring (catalog-svc pattern).
/// • Wires in-memory MassTransit + the PaymentWebhookValidatedConsumer
///   so end-to-end webhook → consumer → published event flow can be
///   asserted via ITestHarness.Published.
/// • Supplies a known Stripe webhook secret in config so the controller
///   can validate test signatures.
/// </summary>
public sealed class PaymentsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestStripeSecret = "whsec_test_secret_phase3d_deterministic";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("payments")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__payments", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", "amqp://guest:guest@localhost:5672/");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");
        Environment.SetEnvironmentVariable("Webhooks__Stripe__WebhookSecret", TestStripeSecret);
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
                ["ConnectionStrings:payments"] = ConnectionString,
                ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"] = "false",
                ["Webhooks:Stripe:WebhookSecret"] = TestStripeSecret,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Production AddMassTransit + AddDomainEventPublisher are skipped
            // by AddInfrastructure when ASPNETCORE_ENVIRONMENT=Test. Wire the
            // in-memory test harness + the consumer so we can assert that
            // (a) the controller publishes PaymentWebhookValidatedEvent, and
            // (b) the consumer publishes PaymentCompletedEvent in response.
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<PaymentWebhookValidatedConsumer>();
            });
            // Test fixture sets harness.TestTimeout in InitializeAsync —
            // EF retry-on-failure (5 × 500ms) plus actual processing can
            // legitimately push the consume duration past the default 5s.
            services.AddDomainEventPublisher();
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Haworks.Payments.Infrastructure.PaymentDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>Convenience: build a Stripe-Signature header for a given payload.</summary>
    public static string SignStripe(string rawPayload, DateTimeOffset? at = null, string? secret = null)
    {
        var time = at ?? DateTimeOffset.UtcNow;
        var unix = time.ToUnixTimeSeconds();
        var signed = $"{unix}.{rawPayload}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret ?? TestStripeSecret));
        var hex = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        return $"t={unix},v1={hex}";
    }
}
