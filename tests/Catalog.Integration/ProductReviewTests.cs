using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Haworks.BuildingBlocks.Testing.Authentication;

namespace Haworks.Catalog.Integration;

[Collection("Catalog Integration")]
public sealed class ProductReviewTests(CatalogWebAppFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.EnsureSchemaAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateReview_returns_201_when_authenticated_and_header_present()
    {
        // Arrange
        var categoryId = await CreateCategoryAsync();
        var productId = await CreateProductAsync(categoryId);
        var request = new
        {
            title = "Great product",
            content = "I really liked it",
            rating = 5,
            authorName = "John Doe"
        };

        // Act
        // TestAuthenticationHandler automatically sets X-User-Id: test-user
        var resp = await _client.PostAsJsonAsync($"/api/products/{productId}/reviews", request);

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateReview_returns_401_when_X_User_Id_header_missing()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var request = new
        {
            title = "Great product",
            content = "I really liked it",
            rating = 5,
            authorName = "John Doe"
        };

        // Use a scheme that authenticates successfully but does NOT set the X-User-Id header.
        var client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "NoHeader";
                    options.DefaultChallengeScheme = "NoHeader";
                }).AddScheme<AuthenticationSchemeOptions, NoHeaderAuthHandler>("NoHeader", _ => { });
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        var resp = await client.PostAsJsonAsync($"/api/products/{productId}/reviews", request);

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> CreateCategoryAsync()
    {
        var name = $"Cat-{Guid.NewGuid():N}";
        var resp = await _client.PostAsJsonAsync("/api/categories",
            new { name, description = "x" });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Guid>();
    }

    private async Task<Guid> CreateProductAsync(Guid categoryId)
    {
        var resp = await _client.PostAsJsonAsync("/api/products", new
        {
            name = $"P-{Guid.NewGuid():N}",
            description = "x",
            unitPrice = 9.99m,
            categoryId,
            initialStock = 10,
        });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Guid>();
    }

    private class NoHeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "some-user") };
            var identity = new ClaimsIdentity(claims, "NoHeader");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "NoHeader");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
