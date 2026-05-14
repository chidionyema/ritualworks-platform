using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.BffWeb.Api.SignalR;
using Haworks.BuildingBlocks.Testing.Authentication;

namespace Haworks.BffWeb.Integration;

/// <summary>
/// WebApplicationFactory for bff-web. No DB (bff-web owns no state).
/// In-memory MassTransit harness with PaymentSessionCreatedConsumer wired
/// so we can publish a synthetic PaymentSessionCreatedEvent and assert it
/// reaches a SignalR client subscribed to the saga group.
/// </summary>
public sealed class BffWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__rabbitmq", "amqp://guest:guest@localhost:5672/");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");

        // Sets both legacy Jwt__* and the new Authentication__Jwks__* keys
        // that AddJwksAuthentication's [Required] + ValidateOnStart needs.
        // Without these the host build trips OptionsValidationException at
        // boot. The test scheme overrides real JWT validation downstream.
        JwtTestDefaults.SetTestEnvironmentVariables();
        return Task.CompletedTask;
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        return base.DisposeAsync().AsTask();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:rabbitmq"] = "amqp://guest:guest@localhost:5672/",
                ["Vault:Enabled"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();

            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<PaymentSessionCreatedConsumer>();
            });
        });
    }
}
