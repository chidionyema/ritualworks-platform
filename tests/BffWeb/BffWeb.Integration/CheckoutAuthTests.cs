using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haworks.BffWeb.Integration;

public sealed class CheckoutAuthTests : IClassFixture<BffWebFactory>
{
    private readonly BffWebFactory _factory;

    public CheckoutAuthTests(BffWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task POST_checkout_returns_401_when_unauthenticated()
    {
        // Arrange: override auth to always fail so the request is anonymous.
        using var noAuthFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddAuthentication("Failing")
                    .AddScheme<AuthenticationSchemeOptions, FailingAuthHandler>("Failing", _ => { });
            });
        });
        var client = noAuthFactory.CreateClient();

        var payload = new
        {
            customerEmail = "attacker@example.com",
            totalAmount = 99.99m,
            items = new[]
            {
                new { productId = Guid.NewGuid(), productName = "Widget", quantity = 1, unitPrice = 99.99m },
            },
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/checkout", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Auth handler that always fails — forces the request to be treated as
    /// unauthenticated so [Authorize] returns 401.
    /// </summary>
    private sealed class FailingAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Fail("forced"));
    }
}
