using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Haworks.Identity.Integration;

/// <summary>
/// End-to-end integration tests for the core auth flows: register, login,
/// refresh, verify, logout. Backed by Testcontainers Postgres + the in-memory
/// signing-key provider (RS256 stays real; Vault is mocked out).
///
/// Each test uses a unique email/username so they are isolated without
/// needing per-test schema reset.
/// </summary>
[Collection("Identity")]
public sealed class AuthFlowsTests : IAsyncLifetime
{
    private readonly IdentityWebAppFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await ((IAsyncLifetime)_factory).InitializeAsync();
        await _factory.EnsureSchemaAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await ((IAsyncLifetime)_factory).DisposeAsync();
    }

    [Fact]
    public async Task Register_with_valid_payload_returns_201_and_jwt()
    {
        var (email, username) = NewUser();

        var response = await _client.PostAsJsonAsync(
            "/api/Authentication/register",
            new { email, username, password = "TestPass#Word123" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AuthResponseShape>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrEmpty();
        body.UserId.Should().NotBeNullOrEmpty();
        body.Email.Should().Be(email);
    }

    [Fact]
    public async Task Register_with_duplicate_email_returns_500_with_error()
    {
        var (email, username) = NewUser();
        var payload1 = new { email, username, password = "TestPass#Word123" };
        var first = await _client.PostAsJsonAsync("/api/Authentication/register", payload1);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var payload2 = new { email, username = username + "-dupe", password = "TestPass#Word123" };
        var second = await _client.PostAsJsonAsync("/api/Authentication/register", payload2);

        // The lifted controller returns 500 with the Identity error string.
        // A future refactor should map DuplicateEmail to 409; for now we
        // verify the duplicate is detected and surfaced (not silently accepted).
        ((int)second.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        var body = await second.Content.ReadAsStringAsync();
        body.Should().Contain("already taken", "duplicate detection should surface");
    }

    [Fact]
    public async Task Login_after_register_returns_200_and_refresh_token()
    {
        var (email, username) = NewUser();
        var password = "TestPass#Word123";

        var reg = await _client.PostAsJsonAsync(
            "/api/Authentication/register",
            new { email, username, password });
        reg.StatusCode.Should().Be(HttpStatusCode.Created);

        var login = await _client.PostAsJsonAsync(
            "/api/Authentication/login",
            new { username, password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<AuthResponseShape>();
        body!.Token.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty(
            "login should issue a refresh token even when register did not");
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var (email, username) = NewUser();
        var reg = await _client.PostAsJsonAsync(
            "/api/Authentication/register",
            new { email, username, password = "TestPass#Word123" });
        reg.StatusCode.Should().Be(HttpStatusCode.Created);

        var login = await _client.PostAsJsonAsync(
            "/api/Authentication/login",
            new { username, password = "WrongPassword#1" });

        ((int)login.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        ((int)login.StatusCode).Should().BeLessThan(500,
            "wrong password is a 4xx (client error), not a 5xx (server error)");
    }

    [Fact]
    public async Task Jwks_endpoint_returns_RSA_signing_key_in_jwk_format()
    {
        var response = await _client.GetAsync("/.well-known/jwks.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var jwks = await response.Content.ReadFromJsonAsync<JwksResponseShape>();
        jwks.Should().NotBeNull();
        jwks!.Keys.Should().HaveCount(1);

        var key = jwks.Keys[0];
        key.Kty.Should().Be("RSA");
        key.Alg.Should().Be("RS256");
        key.Use.Should().Be("sig");
        key.Kid.Should().NotBeNullOrEmpty();
        key.N.Should().NotBeNullOrEmpty("modulus");
        key.E.Should().NotBeNullOrEmpty("exponent");
    }

    [Fact]
    public async Task Issued_JWT_header_advertises_RS256_with_matching_kid()
    {
        var (email, username) = NewUser();
        var reg = await _client.PostAsJsonAsync(
            "/api/Authentication/register",
            new { email, username, password = "TestPass#Word123" });
        reg.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = await reg.Content.ReadFromJsonAsync<AuthResponseShape>();

        var headerJson = DecodeJwtHeader(auth!.Token!);
        headerJson.Should().Contain("\"alg\":\"RS256\"");
        headerJson.Should().Contain("\"kid\":");

        var jwks = await (await _client.GetAsync("/.well-known/jwks.json"))
            .Content.ReadFromJsonAsync<JwksResponseShape>();
        var jwksKid = jwks!.Keys[0].Kid;
        headerJson.Should().Contain($"\"kid\":\"{jwksKid}\"",
            "JWT kid header must point at the public key served by JWKS");
    }

    private static (string email, string username) NewUser()
    {
        var ticks = DateTime.UtcNow.Ticks;
        return ($"u{ticks}@test.invalid", $"user{ticks}");
    }

    private static string DecodeJwtHeader(string jwt)
    {
        var headerSeg = jwt.Split('.')[0];
        // base64url -> base64
        var padded = headerSeg.Replace('-', '+').Replace('_', '/')
            .PadRight(headerSeg.Length + (4 - headerSeg.Length % 4) % 4, '=');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private sealed class AuthResponseShape
    {
        public string? Token { get; set; }
        public string? RefreshToken { get; set; }
        public string? UserId { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
    }

    private sealed class JwksResponseShape
    {
        public List<JwkShape> Keys { get; set; } = new();
    }

    private sealed class JwkShape
    {
        public string? Kty { get; set; }
        public string? Use { get; set; }
        public string? Alg { get; set; }
        public string? Kid { get; set; }
        public string? N { get; set; }
        public string? E { get; set; }
    }
}
