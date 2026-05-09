using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Haworks.BffWeb.Api.SignalR;

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
        Environment.SetEnvironmentVariable("Jwt__Issuer", "test-issuer");
        Environment.SetEnvironmentVariable("Jwt__Audience", "test-audience");
        Environment.SetEnvironmentVariable("Jwt__SigningKeyPem", "-----BEGIN PRIVATE KEY-----\n" +
            "MIIEvAIBADANBgkqhkiG9w0BAQEFAASCBKYwggSiAgEAAoIBAQC8bYBjAt8l7jdU\n" +
            "f+D5Hm0JMPuOO2pjWTKqqJFX9TSpSya8RP84T7fcBCDBUk0YKzhBL+PjuDC6Oz/B\n" +
            "45NR8A9a9sQ0j3v+P/YmopCQ2MAiZAI76ahh7i+TmC274Y7bly+qm16xB9S/GW+A\n" +
            "GKl0HybAYL5EitZdudRvMUTyFk8q9T2a5kHaQPtDQnpxsD6UaKkCw2EC1CIyNWdl\n" +
            "5TQ17Rmd+aboaqJ0bI6NgtTPxnhrUWKUPmLd0+vd8oVqO/BOPJ5Zf4ENxpRlxU2Z\n" +
            "h5wC7CmgUmxeigO7JTi+T6nwnvvD1XfDQusNvd9OwaUaQvhdBehwV/RwoG2NS89l\n" +
            "OnnCv1ezAgMBAAECggEALNLHpcX7G2TNmLZK6DgKrBMQ5EbSCgwf92TeHlRgUJ1l\n" +
            "+4dWRyj/jcEVoadYW5V8blVcGsGoJcUOZ6shUm6O2I63IeG4F0VT4uDtDufg3M15\n" +
            "kpME0Tb97lhXGMiRWT9fwW/wWKCKRWNhmNFFDjCS4VSiLl/wmp8oH8NSqVwRPSBs\n" +
            "P/0/9M9u1aSS5j7BI8eI8vhT2o3cqHC+3QdwpZfdz8oeYJ3Z+NfkXAHYKfW2zIU4\n" +
            "AO08mZMLVVo7vS+2M7NZazEJ5+PdTvij4uzif0SB7FraY/PBpTib6G6iy2PC87ZX\n" +
            "4J9m9E7OF7ETsETNKAa0OyxSQmD0wAx9Z6Dk7rtbMQKBgQD6cvE2RDc5gyw45tqI\n" +
            "utpDIHgfmlI9rn6AEjR4DaQ48igkZWSw7EacU0EZYeKyesGIc1x+y0n1HDRLpepe\n" +
            "+2zq4ERt5I33zYKWJtGmiTAkVdQIHl3tmSHgEw08k4Ke3E42hxb+u/schkm7CHE2\n" +
            "/55tNtp+/Ll6vzXsTo4d+E0+TwKBgQDAmqXuSlksb1bYstdtn1mTqCS4BHoGCnC7\n" +
            "jYT2Zr2sCCMeTNS0TcIWjU9K+6LM1SOQmALfIgNdP7KU8dAulBuUXRxh/5TxR4lx\n" +
            "eEf7Ym7sBYvo+6mMP3fATMPvJzVqWGIu9ZL0PSxOnUM2u4lZX/fZQIJUI9B/xwET\n" +
            "EXJRfBS7XQKBgE1esPHIxR65TTIO7zgKMV9HapSowftYKrA574ee/zqwZIJJ6H9X\n" +
            "nsCwX44N1VC554vVx59MAf78xZMRIIRTO+Sbf8hLMSh6jnsAZwgBnaO7+BLB/tZl\n" +
            "1jc463/pOhMFkAv8U7hCLmMzgReMlh0dfr3SklFklZA7/daQtgrAKGy1AoGAOEv7\n" +
            "vE8XCZnxtJ1xwqUVNcesE+2bDTD4CpovBya4whQOz8h9U8Z2uMjNKIms6FpUbus/\n" +
            "y6DRguwfctHLnBHGjfM5XJusGWpjjjsuLxhye6KTZqJIyKm0gwztKHY5csAq0rcN\n" +
            "IT7QOJpXDyR53RnkBCiK77UYOIEem0g6Nf8iwDECgYBWqsfCBUR++wG5tiORPDC6\n" +
            "Z4e4fbBLvG44KEfPSATnzxEG7G3oxzt05GgPLCq8fJmo6b6+zaiLTk8wWrhtnZng\n" +
            "QREcIzUSD7HcEoJK9g/zGNp3KwqmxgZVlqLkADYgh8KOHjaZ8XSybZpoltDC+f/w\n" +
            "aTrEdzdpSuzJnev1yM0mxg==\n" +
            "-----END PRIVATE KEY-----");
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
            services.AddMassTransitTestHarness(mt =>
            {
                mt.AddConsumer<PaymentSessionCreatedConsumer>();
            });
        });
    }
}
