using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using Haworks.BuildingBlocks.Testing.Authentication;
using Haworks.BuildingBlocks.Testing.Containers;
using Xunit;

namespace Haworks.Identity.Integration;

public sealed class IdentityWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString = await SharedTestPostgres.CreateDatabaseAsync("identity");

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("ConnectionStrings__identity", ConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Key",      "TestEnvJwtKeyAtLeast32CharactersLongForValidationXxxxxx");
        Environment.SetEnvironmentVariable("Jwt__Issuer",   "test-issuer");
        Environment.SetEnvironmentVariable("Jwt__Audience", "test-audience");
        Environment.SetEnvironmentVariable("Vault__Enabled", "false");

        Environment.SetEnvironmentVariable("Authentication__Google__ClientId",        "test-google-client-id");
        Environment.SetEnvironmentVariable("Authentication__Google__ClientSecret",    "test-google-client-secret");
        Environment.SetEnvironmentVariable("Authentication__Microsoft__ClientId",     "test-microsoft-client-id");
        Environment.SetEnvironmentVariable("Authentication__Microsoft__ClientSecret", "test-microsoft-client-secret");
        Environment.SetEnvironmentVariable("Authentication__Facebook__AppId",         "test-facebook-app-id");
        Environment.SetEnvironmentVariable("Authentication__Facebook__AppSecret",     "test-facebook-app-secret");
        Environment.SetEnvironmentVariable("Security__AllowedRedirectHosts__0", "localhost");

        JwtTestDefaults.SetTestEnvironmentVariables();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:identity"] = ConnectionString,
                ["Jwt:Key"]      = "TestEnvJwtKeyAtLeast32CharactersLongForValidationXxxxxx",
                ["Jwt:Issuer"]   = "test-issuer",
                ["Jwt:Audience"] = "test-audience",
                ["Vault:Enabled"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            var existing = services.SingleOrDefault(d =>
                d.ServiceType == typeof(Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider));
            if (existing is not null) services.Remove(existing);

            services.AddSingleton<Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider>(
                new InMemoryJwtSigningKeyProvider());
        });
    }

    public async Task EnsureSchemaAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<Haworks.Identity.Infrastructure.AppIdentityDbContext>();
        await db.Database.MigrateAsync();

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
