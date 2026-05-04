using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Haworks.BuildingBlocks.Testing.Authentication;

/// <summary>
/// No-op authentication handler that always succeeds. Stamps a fixed
/// test principal on every request so [Authorize]-decorated endpoints
/// are usable in integration tests without minting real JWTs.
///
/// Wire it from a test fixture's ConfigureServices:
/// <code>
/// services.AddAuthentication(TestAuthenticationHandler.SchemeName)
///     .AddTestAuth();
/// </code>
/// The fixture must set this scheme as the default — most production
/// services use JwtBearer as the default, so the AddAuthentication call
/// in the fixture overrides that for test-time only.
/// </summary>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string TestUserId = "test-user";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Broad role + permission set so that any policy referenced by a
        // controller passes in the test environment. This is a deliberately
        // permissive principal — narrowing it would force every test fixture
        // to know the policy taxonomy of every other context, which defeats
        // the purpose of a shared test scheme.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, TestUserId),
            new Claim(ClaimTypes.Name, TestUserId),
            new Claim(ClaimTypes.Role, "User"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(ClaimTypes.Role, "ContentUploader"),
            new Claim("permission", "upload_content"),
            new Claim("permission", "manage_content"),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class TestAuthenticationExtensions
{
    /// <summary>
    /// Adds the no-op test scheme using the canonical Scheme name. Use
    /// alongside <c>AddAuthentication(TestAuthenticationHandler.SchemeName)</c>
    /// to also make it the default scheme.
    /// </summary>
    public static AuthenticationBuilder AddTestAuth(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
            TestAuthenticationHandler.SchemeName, _ => { });
}
