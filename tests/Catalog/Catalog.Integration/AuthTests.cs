using System.Net;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Haworks.Catalog.Integration;

[Collection("Catalog Integration")]
public sealed class AuthTests
{
    private readonly CatalogWebAppFactory _factory;

    public AuthTests(CatalogWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateReview_returns_401_when_called_without_bearer_token()
    {
        // Arrange
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Failing";
                    options.DefaultChallengeScheme = "Failing";
                }).AddScheme<AuthenticationSchemeOptions, FailingAuthHandler>("Failing", _ => { });
            });
        }).CreateClient();

        // Act
        var resp = await client.PostAsync($"/api/products/{Guid.NewGuid()}/reviews", null);

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private class FailingAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Fail("Forced failure"));
    }
}
