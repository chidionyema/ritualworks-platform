using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Resilience;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Haworks.Payments.Application.Consumers;
using Haworks.Payments.Api.Webhooks;
using Haworks.Payments.Application.Sagas;
using Haworks.Payments.Domain;
using Haworks.Payments.Infrastructure;
using Haworks.Payments.Infrastructure.Options;

namespace Haworks.Payments.Integration;

public class PaymentsWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestStripeSecret = "whsec_test";

    private string ConnString { get; set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnString = await SharedTestPostgres.CreateDatabaseAsync("payments");

        // Top-level Program.cs reads builder.Configuration before WAF's
        // ConfigureAppConfiguration fires, so secrets must already be visible
        // as env vars by the time the host starts building.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__payments", ConnString);
        Environment.SetEnvironmentVariable("Webhooks__Stripe__WebhookSecret", TestStripeSecret);
        Environment.SetEnvironmentVariable("PaymentProviders__Active", "Stripe");
        Environment.SetEnvironmentVariable("PaymentProviders__Stripe__WebhookSecret", TestStripeSecret);
        Environment.SetEnvironmentVariable("PaymentProviders__Stripe__SecretKey", "sk_test_dummy");

        // Production AddPlatformAuthentication runs at boot and requires Jwt:* config.
        // Set test-grade defaults — TestAuthenticationHandler still wins as the default
        // scheme via ConfigureTestServices below.
        JwtTestDefaults.SetTestEnvironmentVariables();
    }

    public new Task DisposeAsync()
    {
        // Shared Postgres container outlives the fixture intentionally.
        return Task.CompletedTask;
    }

    public async Task EnsureSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.OpenConnectionAsync();
        try { await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS payments;"); }
        finally { await db.Database.CloseConnectionAsync(); }
        await db.Database.EnsureCreatedAsync();
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
                // The RefundSaga calls .Schedule() on entry to Requested state.
                // Without a scheduler the saga faults and never transitions, causing
                // sagaHarness.Consumed.Any<RefundRequestedEvent>() to return false.
                mt.AddDelayedMessageScheduler();

                // Only register consumers that don't interfere with manual saga event flow.
                // ProviderRefundInitiationRequestedConsumer and SubscriptionRenewalRequestedConsumer
                // are omitted because they auto-process saga-published events (calling real Stripe)
                // and race with manually-published test events.
                mt.AddConsumer<PaymentWebhookValidatedConsumer>();
                mt.AddConsumer<PaymentSessionRequestedConsumer>();
                mt.AddSagaStateMachine<RefundSaga, RefundSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ExistingDbContext<PaymentDbContext>();
                        r.UsePostgres();
                    });
                mt.AddSagaStateMachine<SubscriptionSaga, SubscriptionSagaState>()
                    .EntityFrameworkRepository(r =>
                    {
                        r.ExistingDbContext<PaymentDbContext>();
                        r.UsePostgres();
                    });
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
