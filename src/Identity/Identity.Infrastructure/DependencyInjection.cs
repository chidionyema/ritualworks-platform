using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Haworks.BuildingBlocks.Persistence;
using Haworks.BuildingBlocks.Caching;
using Haworks.BuildingBlocks.Vault;

namespace Haworks.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // L1+L2 hybrid cache used by TokenRevocationService for fast JTI lookups.
        // L2 backing: in-memory fallback for now (single-process dev). When Aspire
        // injects ConnectionStrings__redis, swap to AddStackExchangeRedisCache.
        services.AddInMemoryDistributedCache();
        services.AddHybridCache();

        // Connection string resolution order:
        //   1. Aspire-injected ConnectionStrings__identity (preferred — Aspire owns the lifecycle)
        //   2. Standard ConnectionStrings:DefaultConnection (override / standalone runs)
        var connectionString = configuration.GetConnectionString("identity")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No identity database connection string. Expected 'ConnectionStrings:identity' " +
                "(Aspire-injected) or 'ConnectionStrings:DefaultConnection'.");

        services.AddDbContext<AppIdentityDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString);

            // Vault dynamic credentials interceptor — optional for now. When the
            // VaultService stack is wired in (Phase 1 follow-up), the interceptor
            // is registered via services.AddVaultIntegration() and resolved here
            // to swap the static password for an issued one. Until then, plain
            // Aspire-injected creds work fine for dev.
            var interceptor = sp.GetService<DynamicCredentialsConnectionInterceptor>();
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });

        services.AddIdentity<User, IdentityRole>(options =>
                {
                    // Sensible defaults; Phase 1 follow-up moves these to Options
                    options.Password.RequiredLength = 8;
                    options.Password.RequireDigit = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = false;
                    options.Lockout.MaxFailedAccessAttempts = 5;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                    options.User.RequireUniqueEmail = true;
                })
                .AddEntityFrameworkStores<AppIdentityDbContext>()
                .AddDefaultTokenProviders();

        // Default authentication scheme: JwtBearer for protected API endpoints.
        // External providers (Google/Microsoft/Facebook) attach below as
        // ADDITIONAL schemes used only by the explicit Challenge() flow in
        // the external-auth controller. [Authorize] without an explicit
        // scheme name therefore expects a Bearer token and returns 401 (not
        // a 302 cookie redirect) for unauthenticated requests.
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme    = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
        })
            .AddJwtBearer(options =>
            {
                // TokenValidationParameters are filled in via JwtBearer
                // PostConfigureOptions registered at the bottom of this
                // method — the IssuerSigningKey comes from the Vault-backed
                // signing-key provider which is initialized in Program.cs
                // AFTER Build() but BEFORE the first request.
                options.RequireHttpsMetadata = false;
                options.SaveToken = true;

                // After signature/issuer/audience validation, ALSO check our
                // JTI revocation list — signature-only validation cannot catch
                // a JWT that the user explicitly logged out. Per CLAUDE.md
                // security mandate: "ALL authentication handlers MUST verify
                // the token's jti against ITokenRevocationService."
                options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var jti = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
                        if (string.IsNullOrEmpty(jti)) return;
                        var revocation = context.HttpContext.RequestServices
                            .GetRequiredService<ITokenRevocationService>();
                        if (await revocation.IsTokenRevokedAsync(jti, context.HttpContext.RequestAborted))
                        {
                            context.Fail("Token has been revoked.");
                        }
                    },
                };
            })
            .AddGoogle(options =>
            {
                var section = configuration.GetSection("Authentication:Google");
                options.ClientId     = section["ClientId"]     ?? throw new InvalidOperationException("Google ClientId missing");
                options.ClientSecret = section["ClientSecret"] ?? throw new InvalidOperationException("Google ClientSecret missing");
                options.CallbackPath = new Microsoft.AspNetCore.Http.PathString("/api/external-authentication/google-callback");
                options.SaveTokens   = true;
                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "id");
                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name,           "name");
                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email,          "email");
                options.ClaimActions.MapJsonKey("picture", "picture");
            })
            .AddMicrosoftAccount(options =>
            {
                var section = configuration.GetSection("Authentication:Microsoft");
                options.ClientId     = section["ClientId"]     ?? throw new InvalidOperationException("Microsoft ClientId missing");
                options.ClientSecret = section["ClientSecret"] ?? throw new InvalidOperationException("Microsoft ClientSecret missing");
                options.CallbackPath = new Microsoft.AspNetCore.Http.PathString("/api/external-authentication/microsoft-callback");
                options.SaveTokens   = true;
            })
            .AddFacebook(options =>
            {
                var section = configuration.GetSection("Authentication:Facebook");
                options.AppId        = section["AppId"]        ?? throw new InvalidOperationException("Facebook AppId missing");
                options.AppSecret    = section["AppSecret"]    ?? throw new InvalidOperationException("Facebook AppSecret missing");
                options.CallbackPath = new Microsoft.AspNetCore.Http.PathString("/api/external-authentication/facebook-callback");
                options.SaveTokens   = true;
            });

        // Domain repositories (EF-backed implementations from IdentityRepositories.cs).
        services.AddScoped<IUserRepository, IdentityUserRepository>();
        services.AddScoped<IUserProfileRepository, IdentityUserProfileRepository>();
        services.AddScoped<IRefreshTokenRepository, IdentityRefreshTokenRepository>();

        services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // RSA signing keypair for JWT (RS256). Provider reads/writes
        // secret/identity/jwt-signing in Vault. Singleton because the
        // keypair lives for the process lifetime; Program.cs must call
        // VaultJwtSigningKeyProvider.InitializeAsync once at startup
        // before request handling.
        services.AddSingleton<IVaultAppRoleAuthenticator, VaultAppRoleAuthenticator>();
        services.AddSingleton<IJwtSigningKeyProvider>(sp =>
        {
            var cfg          = sp.GetRequiredService<IConfiguration>();
            var address      = cfg["Vault:Address"]      ?? throw new InvalidOperationException("Vault:Address missing");
            var roleIdPath   = cfg["Vault:RoleIdPath"]   ?? throw new InvalidOperationException("Vault:RoleIdPath missing");
            var secretIdPath = cfg["Vault:SecretIdPath"] ?? throw new InvalidOperationException("Vault:SecretIdPath missing");
            var roleId       = File.ReadAllText(roleIdPath).Trim();
            var secretId     = File.ReadAllText(secretIdPath).Trim();
            var auth         = sp.GetRequiredService<IVaultAppRoleAuthenticator>();
            return new VaultJwtSigningKeyProvider(address, "identity", auth, roleId, secretId);
        });

        // Late-bind JwtBearer.TokenValidationParameters from IJwtSigningKey
        // Provider on first JwtBearerOptions access (PostConfigure runs lazily).
        services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<
            Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>(sp =>
            new JwtBearerPostConfigureOptions(sp));

        return services;
    }
}

/// <summary>
/// Late-binds JwtBearerOptions.TokenValidationParameters using the
/// IJwtSigningKeyProvider, which is itself a singleton that needs Initialize-
/// Async called once at startup. This indirection lets us resolve the
/// SigningKey lazily on first JwtBearerOptions access — by then the provider
/// is initialized.
/// </summary>
internal sealed class JwtBearerPostConfigureOptions
    : Microsoft.Extensions.Options.IPostConfigureOptions<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>
{
    private readonly IServiceProvider _sp;
    public JwtBearerPostConfigureOptions(IServiceProvider sp) => _sp = sp;

    public void PostConfigure(string? name, Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions options)
    {
        var keyProvider = _sp.GetRequiredService<Haworks.BuildingBlocks.Vault.IJwtSigningKeyProvider>();
        var jwt = _sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Haworks.Identity.Application.Options.JwtOptions>>().Value;

        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = keyProvider.SigningKey,
            ValidAlgorithms          = new[] { Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256 },
            ValidateIssuer           = true,
            ValidIssuer              = jwt.Issuer,
            ValidateAudience         = true,
            ValidAudience            = jwt.Audience,
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.FromSeconds(Haworks.Identity.Application.Constants.AuthConstants.ClockSkewToleranceSeconds),
        };
    }
}
