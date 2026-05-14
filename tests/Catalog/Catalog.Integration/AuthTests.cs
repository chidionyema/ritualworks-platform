using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
public sealed class AuthTests : IAsyncLifetime
{
    private readonly CatalogWebAppFactory _factory;

    public AuthTests(CatalogWebAppFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync() => await _factory.EnsureSchemaAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient CreateUnauthenticatedClient() =>
        _factory.WithWebHostBuilder(builder =>
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

    [Fact]
    public async Task CreateReview_returns_401_when_called_without_bearer_token()
    {
        var client = CreateUnauthenticatedClient();
        var resp = await client.PostAsync($"/api/products/{Guid.NewGuid()}/reviews", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_product_returns_401_when_unauthenticated()
    {
        var client = CreateUnauthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/products", new
        {
            name = "Unauth-product",
            description = "x",
            unitPrice = 1.00m,
            categoryId = Guid.NewGuid(),
            initialStock = 1,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DELETE_product_returns_401_when_unauthenticated()
    {
        var client = CreateUnauthenticatedClient();
        var resp = await client.DeleteAsync($"/api/products/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_products_returns_200_when_unauthenticated()
    {
        var client = CreateUnauthenticatedClient();
        var resp = await client.GetAsync("/api/products");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_category_returns_401_when_unauthenticated()
    {
        var client = CreateUnauthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/categories", new
        {
            name = "Unauth-cat",
            description = "x",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private class FailingAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Fail("Forced failure"));
    }
}
