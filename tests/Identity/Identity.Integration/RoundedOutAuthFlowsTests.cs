using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Haworks.Identity.Integration;

/// <summary>
/// Round-out coverage for the auth surface beyond the basic register/login.
///
/// Covers:
///   • POST /Authentication/refresh-token       — happy path + invalid refresh
///   • POST /Authentication/logout              — revokes JTI; subsequent verify-token rejects
///   • GET  /Authentication/verify-token        — with valid bearer + after revocation
///   • GET  /Authentication/csrf-token          — anti-CSRF token issuance
///   • GET  /external-authentication/providers  — list registered OAuth providers
///   • GET  /external-authentication/challenge/{provider} — OAuth2 challenge redirect
/// </summary>
[Collection("Identity")]
public sealed class RoundedOutAuthFlowsTests : IAsyncLifetime
{
    private readonly IdentityWebAppFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await ((IAsyncLifetime)_factory).InitializeAsync();
        await _factory.EnsureSchemaAsync();
        _client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            // Don't auto-follow Google's 302 — we want to inspect the redirect.
            AllowAutoRedirect = false,
        });
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await ((IAsyncLifetime)_factory).DisposeAsync();
    }

    [Fact]
    public async Task RefreshToken_with_valid_pair_returns_new_jwt()
    {
        // Arrange — register + login to get a valid (token, refreshToken) pair
        var (email, username) = NewUser();
        await _client.PostAsJsonAsync("/api/Authentication/register",
            new { email, username, password = "TestPass#Word123" });
        var login = await _client.PostAsJsonAsync("/api/Authentication/login",
            new { username, password = "TestPass#Word123" });
        var tokens = await login.Content.ReadFromJsonAsync<AuthResponseShape>();

        // Act
        var refresh = await _client.PostAsJsonAsync(
            "/api/Authentication/refresh-token",
            new { accessToken = tokens!.Token, refreshToken = tokens.RefreshToken });

        // Assert — should return a NEW token (different jti) and a new refresh token
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await refresh.Content.ReadFromJsonAsync<AuthResponseShape>();
        refreshed!.Token.Should().NotBeNullOrEmpty();
        refreshed.Token.Should().NotBe(tokens.Token, "refresh should mint a fresh JWT");
        refreshed.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RefreshToken_with_garbage_refresh_token_returns_4xx()
    {
        var (email, username) = NewUser();
        await _client.PostAsJsonAsync("/api/Authentication/register",
            new { email, username, password = "TestPass#Word123" });
        var login = await _client.PostAsJsonAsync("/api/Authentication/login",
            new { username, password = "TestPass#Word123" });
        var tokens = await login.Content.ReadFromJsonAsync<AuthResponseShape>();

        var refresh = await _client.PostAsJsonAsync(
            "/api/Authentication/refresh-token",
            new { accessToken = tokens!.Token, refreshToken = "garbage-not-a-real-refresh-token" });

        ((int)refresh.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        ((int)refresh.StatusCode).Should().BeLessThan(500,
            "invalid refresh token is a 4xx (client error), not a 5xx");
    }

    [Fact]
    public async Task Logout_revokes_jti_and_subsequent_verify_token_is_rejected()
    {
        // Arrange — register + login
        var (email, username) = NewUser();
        await _client.PostAsJsonAsync("/api/Authentication/register",
            new { email, username, password = "TestPass#Word123" });
        var login = await _client.PostAsJsonAsync("/api/Authentication/login",
            new { username, password = "TestPass#Word123" });
        var tokens = await login.Content.ReadFromJsonAsync<AuthResponseShape>();

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.Token);

        // verify-token works BEFORE logout
        var verifyBefore = await _client.GetAsync("/api/Authentication/verify-token");
        verifyBefore.StatusCode.Should().Be(HttpStatusCode.OK,
            "verify-token must succeed for a freshly minted JWT");

        // Act — logout (revokes the JTI server-side)
        var logout = await _client.PostAsJsonAsync(
            "/api/Authentication/logout",
            new { accessToken = tokens.Token, refreshToken = tokens.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert — verify-token must now reject the same JWT (revoked)
        var verifyAfter = await _client.GetAsync("/api/Authentication/verify-token");
        ((int)verifyAfter.StatusCode).Should().BeGreaterThanOrEqualTo(400,
            "verify-token must reject a JWT whose JTI was revoked by logout");
    }

    [Fact]
    public async Task VerifyToken_without_bearer_returns_401()
    {
        // No Authorization header set
        var response = await _client.GetAsync("/api/Authentication/verify-token");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CsrfToken_endpoint_returns_200_with_token()
    {
        var response = await _client.GetAsync("/api/Authentication/csrf-token");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Body shape is controller-defined; just check it returns SOMETHING
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExternalProviders_endpoint_returns_Google_Microsoft_Facebook()
    {
        var response = await _client.GetAsync("/api/external-authentication/providers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Google");
        body.Should().Contain("Microsoft");
        body.Should().Contain("Facebook");
    }

    [Fact]
    public async Task ExternalChallenge_invalid_provider_returns_400()
    {
        var response = await _client.GetAsync(
            "/api/external-authentication/challenge/notreal?redirectUrl=/cb");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExternalChallenge_Google_returns_302_to_accounts_google_com()
    {
        var response = await _client.GetAsync(
            "/api/external-authentication/challenge/Google?redirectUrl=https://localhost/cb");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.Host.Should().Be("accounts.google.com",
            "Google challenge must redirect to Google's OAuth2 endpoint");
        response.Headers.Location!.Query.Should().Contain("client_id=",
            "redirect must include the OAuth2 client_id");
        response.Headers.Location!.Query.Should().Contain("code_challenge=",
            "redirect must use PKCE");
    }

    private static (string email, string username) NewUser()
    {
        var ticks = DateTime.UtcNow.Ticks;
        return ($"u{ticks}@test.invalid", $"user{ticks}");
    }

    private sealed class AuthResponseShape
    {
        public string? Token { get; set; }
        public string? RefreshToken { get; set; }
        public string? UserId { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
    }
}
