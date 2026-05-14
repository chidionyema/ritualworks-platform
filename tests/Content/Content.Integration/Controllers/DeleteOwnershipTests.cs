using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Haworks.Content.Application.DTOs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Haworks.Content.Integration.Controllers;

/// <summary>
/// Verifies that a non-owner cannot delete another user's content.
/// </summary>
public sealed class DeleteOwnershipTests : IClassFixture<ContentWebAppFactory>, IAsyncLifetime
{
    private readonly ContentWebAppFactory _factory;
    private readonly HttpClient _ownerClient;

    public DeleteOwnershipTests(ContentWebAppFactory factory)
    {
        _factory = factory;
        // Default client uses TestAuthenticationHandler which sets X-User-Id = "test-user".
        _ownerClient = factory.CreateClient();
    }

    public Task InitializeAsync() => _factory.EnsureSchemaAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Delete_content_returns_403_when_non_owner()
    {
        // Arrange: create content as the default "test-user".
        var initResp = await _ownerClient.PostAsJsonAsync("/api/v1/content/uploads", new InitUploadRequestDto(
            EntityId: Guid.NewGuid(),
            EntityType: "Product",
            FileName: "owned.png",
            ContentType: "image/png",
            TotalSize: 1024));
        initResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var init = await initResp.Content.ReadFromJsonAsync<InitUploadResultDto>();
        init.Should().NotBeNull();
        var contentId = init!.ContentId;

        // Act: try to delete as a DIFFERENT user.
        using var nonOwnerFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddAuthentication("NonOwner")
                    .AddScheme<AuthenticationSchemeOptions, NonOwnerAuthHandler>("NonOwner", _ => { });
            });
        });
        var nonOwnerClient = nonOwnerFactory.CreateClient();
        var deleteResp = await nonOwnerClient.DeleteAsync($"/api/v1/content/{contentId}");

        // Assert
        deleteResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Auth handler that authenticates as a different user ("attacker-user")
    /// to test ownership enforcement.
    /// </summary>
    private sealed class NonOwnerAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            const string differentUserId = "attacker-user";
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, differentUserId),
                new Claim(ClaimTypes.Name, differentUserId),
                new Claim(ClaimTypes.Role, "User"),
                new Claim(ClaimTypes.Role, "ContentUploader"),
            };

            // Simulate BFF forwarding header for the different user.
            if (!Context.Request.Headers.ContainsKey("X-User-Id"))
            {
                Context.Request.Headers["X-User-Id"] = differentUserId;
            }

            var identity = new ClaimsIdentity(claims, "NonOwner");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "NonOwner");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
