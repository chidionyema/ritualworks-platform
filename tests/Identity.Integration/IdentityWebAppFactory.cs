using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using Testcontainers.PostgreSql;
using Xunit;

namespace Haworks.Identity.Integration;

/// <summary>
/// WebApplicationFactory specialized for identity-svc integration tests.
///
/// Spins up its own postgres via Testcontainers (no shared Aspire).
/// Sets ASPNETCORE_ENVIRONMENT=Test which:
///   • Skips the EF auto-migrate-at-startup block (we apply migrations
///     here in IAsyncLifetime so each test class gets a clean schema).
///   • Skips the Vault bootstrap (we inject Jwt:* config + a fake
///     IJwtSigningKeyProvider directly so tests don't need a Vault container).
///
/// Uses an in-memory IJwtSigningKeyProvider so RS256 still works without
/// a real Vault. Production flow is exercised by the cross-system smoke
/// tests under tests/E2E/.
/// </summary>
public sealed class IdentityWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("identity")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Set env vars BEFORE WebApplicationFactory builds the host. With
        // top-level-statement Program.cs, the user's Main runs to construct
        // the WebApplicationBuilder before WAF's ConfigureAppConfiguration
        // hooks fire — so config set via ConfigureAppConfiguration is not
        // visible to AddInfrastructure(builder.Configuration). Env vars are
        // read by ConfigurationBuilder.AddEnvironmentVariables which IS
        // active by default inside CreateBuilder, so they ARE visible.
        Environment.SetEnvironmentVariable("ConnectionStrings__identity", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Key",      "TestEnvJwtKeyAtLeast32CharactersLongForValidationXxxxxx");
        Environment.SetEnvironmentVariable("Jwt__Issuer",   "test-issuer");
        Environment.SetEnvironmentVariable("Jwt__Audience", "test-audience");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");

        // External auth providers — dummy values so AddGoogle/Microsoft/Facebook
        // construction does not throw. Tests do not actually exchange OAuth
        // codes with real providers; we only verify our /challenge route emits
        // a proper 302 redirect with correct PKCE state.
        Environment.SetEnvironmentVariable("Authentication__Google__ClientId",        "test-google-client-id");
        Environment.SetEnvironmentVariable("Authentication__Google__ClientSecret",    "test-google-client-secret");
        Environment.SetEnvironmentVariable("Authentication__Microsoft__ClientId",     "test-microsoft-client-id");
        Environment.SetEnvironmentVariable("Authentication__Microsoft__ClientSecret", "test-microsoft-client-secret");
        Environment.SetEnvironmentVariable("Authentication__Facebook__AppId",         "test-facebook-app-id");
        Environment.SetEnvironmentVariable("Authentication__Facebook__AppSecret",     "test-facebook-app-secret");

        // Security options for the external-auth controller's redirect-host allow-list.
        Environment.SetEnvironmentVariable("Security__AllowedRedirectHosts__0", "localhost");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Postgres from Testcontainers
                ["ConnectionStrings:identity"] = ConnectionString,

                // JWT options — Test fixture supplies values directly,
                // bypassing the production VaultConfigBootstrap path
                ["Jwt:Key"]      = "TestEnvJwtKeyAtLeast32CharactersLongForValidationXxxxxx",
                ["Jwt:Issuer"]   = "test-issuer",
                ["Jwt:Audience"] = "test-audience",

                // Disable the production Vault bootstrap branch in Program.cs
                ["Vault:Enabled"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the real Vault-backed signing-key provider with a
            // fake using a test-only RSA keypair generated in memory.
            // Avoids Vault dependency in unit/integration tests.
            var existing = services.SingleOrDefault(d =>
                d.ServiceType == typeof(Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider));
            if (existing is not null) services.Remove(existing);

            services.AddSingleton<Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider>(
                new InMemoryJwtSigningKeyProvider());
        });
    }

    /// <summary>Apply EF migrations before tests run. Call once per fixture lifetime.</summary>
    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Haworks.Identity.Infrastructure.AppIdentityDbContext>();
        await db.Database.MigrateAsync();

        // Seed roles (Program.cs's seed step is skipped in Test environment).
        var roleManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();
        foreach (var roleName in new[] { "Admin", "ContentUploader", "User" })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(roleName));
            }
        }
    }
}

/// <summary>
/// Test-only IJwtSigningKeyProvider that generates a fresh RSA-2048 keypair
/// in memory. No Vault dependency. Lives for the lifetime of the test fixture.
/// </summary>
internal sealed class InMemoryJwtSigningKeyProvider : Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider
{
    private readonly RSA _rsa;
    public string KeyId { get; }
    public RsaSecurityKey SigningKey { get; }
    public JsonWebKey PublicJwk { get; }

    public InMemoryJwtSigningKeyProvider()
    {
        _rsa = RSA.Create(2048);
        KeyId = "test-" + Guid.NewGuid().ToString("N");
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };

        var publicParams = _rsa.ExportParameters(includePrivateParameters: false);
        PublicJwk = new JsonWebKey
        {
            Kty = "RSA",
            Use = "sig",
            Alg = SecurityAlgorithms.RsaSha256,
            Kid = KeyId,
            N = Base64UrlEncoder.Encode(publicParams.Modulus!),
            E = Base64UrlEncoder.Encode(publicParams.Exponent!),
        };
    }
}
