using System.Net;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Haworks.Search.Integration;

[Collection("Search Integration")]
public sealed class AuthTests
{
    private readonly SearchWebAppFactory _factory;

    public AuthTests(SearchWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Search_returns_401_when_called_without_bearer_token()
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
        var resp = await client.GetAsync("/search?q=test");

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
