using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.Payments.Application.Consumers;
using Haworks.Payments.Api.Webhooks;
using Haworks.Payments.Infrastructure;
using Haworks.Payments.Infrastructure.Options;

namespace Haworks.Payments.Integration;

public class PaymentsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("payments")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public const string TestStripeSecret = "whsec_test";

    private string ConnString => _dbContainer.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        // Top-level Program.cs reads builder.Configuration before WAF's
        // ConfigureAppConfiguration fires, so secrets must already be visible
        // as env vars by the time the host starts building.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__payments", ConnString);
        Environment.SetEnvironmentVariable("Webhooks__Stripe__WebhookSecret", TestStripeSecret);
        Environment.SetEnvironmentVariable("PaymentProviders__Active", "Stripe");
        Environment.SetEnvironmentVariable("PaymentProviders__Stripe__WebhookSecret", TestStripeSecret);
        Environment.SetEnvironmentVariable("PaymentProviders__Stripe__SecretKey", "sk_test_dummy");
    }

    public new async Task DisposeAsync()
    {
        await _dbContainer.StopAsync();
    }

    public async Task EnsureSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS payments;");
            await db.Database.EnsureCreatedAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:payments"] = ConnString,
                ["Webhooks:Stripe:WebhookSecret"] = TestStripeSecret,
                ["PaymentProviders:Active"] = "Stripe",
                ["PaymentProviders:Stripe:WebhookSecret"] = TestStripeSecret,
                ["PaymentProviders:Stripe:SecretKey"] = "sk_test_dummy",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigureAll<WebhookOptions>(opt =>
            {
                opt.Stripe.WebhookSecret = TestStripeSecret;
            });

            services.PostConfigureAll<PaymentProviderOptions>(opt =>
            {
                opt.Active = Haworks.Contracts.Payments.PaymentProvider.Stripe;
                opt.Stripe.WebhookSecret = TestStripeSecret;
            });

            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<PaymentWebhookValidatedConsumer>();
            });

            services.AddDomainEventPublisher();
            services.AddSingleton<ITelemetryService>(_ => NullTelemetryService.Instance);
            services.AddSingleton<IResiliencePolicyFactory, ResiliencePolicyFactory>();

            // [Authorize]-decorated endpoints need an authentication scheme.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();
        });
    }

    public static string SignStripe(string rawPayload, DateTimeOffset? at = null, string? secret = null)
    {
        var unix = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signed = $"{unix}.{rawPayload}";
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret ?? TestStripeSecret));
        var hex = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        return $"t={unix},v1={hex}";
    }
}
