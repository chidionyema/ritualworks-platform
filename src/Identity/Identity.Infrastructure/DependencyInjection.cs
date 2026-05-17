using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MassTransit;
using Haworks.BuildingBlocks.Caching;
using Haworks.BuildingBlocks.Messaging;
using Haworks.BuildingBlocks.Vault;
using Haworks.Identity.Infrastructure.Authentication;
using Npgsql;

namespace Haworks.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment env)
    {
        // Connection string resolution order:
        //   1. Aspire-injected ConnectionStrings__identity (preferred — Aspire owns the lifecycle)
        //   2. Standard ConnectionStrings:DefaultConnection (override / standalone runs)
        var connectionString = configuration.GetConnectionString("identity")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "No identity database connection string. Expected 'ConnectionStrings:identity' " +
                "(Aspire-injected) or 'ConnectionStrings:DefaultConnection'.");

        // Vault DI: registers IVaultService + supporting types from
        // BuildingBlocks.Vault. Required for the demo vault-rotation
        // endpoint to do real per-stage RefreshCredentials calls
        // through the registered IVaultService instance.
        var vaultEnabled = configuration.GetValue("Vault:Enabled", false)
            && !env.IsEnvironment("Test");
        if (vaultEnabled)
        {
            services.AddVaultIntegration(configuration);
            services.AddVaultNpgsqlDataSource(connectionString, "haworks-identity");
        }

        // L1+L2 hybrid cache used by TokenRevocationService for fast JTI lookups.
        // L2 backing: in-memory fallback for now (single-process dev). When Aspire
        // injects ConnectionStrings__redis, swap to AddStackExchangeRedisCache.
        services.AddInMemoryDistributedCache();
        services.AddHybridCache();

        services.AddDbContext<AppIdentityDbContext>((sp, options) =>
        {
            if (vaultEnabled)
            {
                options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>());
            }
            else
            {
                options.UseNpgsql(connectionString);
            }
        });

        services.AddIdentity<User, IdentityRole>(options =>
                {
                    // Sensible defaults; Phase 1 follow-up moves these to Options
                    options.Password.RequiredLength = 8;
                    options.Password.RequireDigit = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = true;
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
        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme    = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme;
        });

        authBuilder.AddJwtBearer(options =>
        {
            // TokenValidationParameters are filled in via JwtBearer
            // PostConfigureOptions registered at the bottom of this
            // method — the IssuerSigningKey comes from IJwtSigningKey
            // Provider which is initialized in Program.cs AFTER Build()
            // but BEFORE the first request.
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
        });

        // External providers are optional — only register the ones the
        // operator has supplied credentials for. Lets Identity boot in
        // environments (e.g. Fly without Vault) where OAuth isn't wired.
        var googleSection = configuration.GetSection("Authentication:Google");
        if (!string.IsNullOrWhiteSpace(googleSection["ClientId"]))
        {
            authBuilder.AddGoogle(options =>
            {
                options.ClientId     = googleSection["ClientId"]!;
                options.ClientSecret = googleSection["ClientSecret"]
                    ?? throw new InvalidOperationException("Authentication:Google:ClientSecret missing");
                options.CallbackPath = new Microsoft.AspNetCore.Http.PathString("/api/external-authentication/google-callback");
                options.SaveTokens   = true;
                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "id");
                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name,           "name");
                options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email,          "email");
                options.ClaimActions.MapJsonKey("picture", "picture");
            });
        }

        var microsoftSection = configuration.GetSection("Authentication:Microsoft");
        if (!string.IsNullOrWhiteSpace(microsoftSection["ClientId"]))
        {
            authBuilder.AddMicrosoftAccount(options =>
            {
                options.ClientId     = microsoftSection["ClientId"]!;
                options.ClientSecret = microsoftSection["ClientSecret"]
                    ?? throw new InvalidOperationException("Authentication:Microsoft:ClientSecret missing");
                options.CallbackPath = new Microsoft.AspNetCore.Http.PathString("/api/external-authentication/microsoft-callback");
                options.SaveTokens   = true;
            });
        }

        var facebookSection = configuration.GetSection("Authentication:Facebook");
        if (!string.IsNullOrWhiteSpace(facebookSection["AppId"]))
        {
            authBuilder.AddFacebook(options =>
            {
                options.AppId     = facebookSection["AppId"]!;
                options.AppSecret = facebookSection["AppSecret"]
                    ?? throw new InvalidOperationException("Authentication:Facebook:AppSecret missing");
                options.CallbackPath = new Microsoft.AspNetCore.Http.PathString("/api/external-authentication/facebook-callback");
                options.SaveTokens   = true;
            });
        }

        // Dual-key JWT validator for overlap window during key rotation.
        services.AddSingleton<Haworks.Identity.Application.Services.DualKeyJwtValidator>();

        // Domain repositories (EF-backed implementations from IdentityRepositories.cs).
        services.AddScoped<IUserRepository, IdentityUserRepository>();
        services.AddScoped<IUserProfileRepository, IdentityUserProfileRepository>();
        services.AddScoped<IRefreshTokenRepository, IdentityRefreshTokenRepository>();

        services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        // RSA signing keypair for JWT (RS256). Two paths:
        //  - Vault:Enabled=true  → RotatingJwtSigningKeyRing polls
        //                          secret/identity/jwt#signing_key from Vault
        //                          every 30s and maintains a key ring with a
        //                          1-hour grace for the previous Active key
        //                          (zero-downtime rotation). Both the ring
        //                          (singleton + IHostedService) and the
        //                          IJwtSigningKeyProvider adapter are
        //                          registered. /.well-known/jwks.json is
        //                          served from the ring's full key set.
        //  - Vault:Enabled=false → ConfigJwtSigningKeyProvider reads
        //                          Jwt:SigningKeyPem (raw or base64 PEM) from
        //                          configuration. Used on Fly where there is
        //                          no Vault container. No rotation in this
        //                          path — restart picks up new config.
        // Singleton — keypair / ring lives for the process lifetime.
        services.AddSingleton<IVaultAppRoleAuthenticator, VaultAppRoleAuthenticator>();
        if (vaultEnabled)
        {
            // The rotating ring depends on IVaultService (already registered
            // by AddVaultIntegration above). Register it as both the ring
            // interface and as an IHostedService so the background poll loop
            // starts at app boot.
            services.AddSingleton<RotatingJwtSigningKeyRing>();
            services.AddSingleton<IJwtSigningKeyRing>(sp => sp.GetRequiredService<RotatingJwtSigningKeyRing>());
            services.AddHostedService(sp => sp.GetRequiredService<RotatingJwtSigningKeyRing>());

            // Adapter — JwtTokenService and JwtBearer post-config still
            // consume IJwtSigningKeyProvider; the adapter delegates to the
            // ring's current Active entry.
            services.AddSingleton<IJwtSigningKeyProvider>(sp =>
                new RotatingJwtSigningKeyProvider(sp.GetRequiredService<IJwtSigningKeyRing>()));
        }
        else
        {
            services.AddSingleton<IJwtSigningKeyProvider>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var pem = cfg["Jwt:SigningKeyPem"]
                    ?? throw new InvalidOperationException(
                        "Jwt:SigningKeyPem is required when Vault:Enabled=false. " +
                        "Provide an RSA PEM private key (raw or base64-encoded).");
                var keyId = cfg["Jwt:KeyId"] ?? "config-1";
                return new ConfigJwtSigningKeyProvider(pem, keyId);
            });
        }

        // Late-bind JwtBearer.TokenValidationParameters from IJwtSigningKey
        // Provider on first JwtBearerOptions access (PostConfigure runs lazily).
        services.AddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<
            Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>(sp =>
            new JwtBearerPostConfigureOptions(sp));

        // MassTransit + IDomainEventPublisher for the vault-rotation demo's
        // VaultRotationStageEvent publishes. Identity has no DB writes that
        // need to be transactionally bound to these events, so we skip the
        // EF outbox and publish straight to RabbitMQ — IDomainEventPublisher
        // routes through IPublishEndpoint either way. Skipped in Test
        // environment so the unit/integration fixtures can supply their own
        // bus or dispense with one entirely.
        if (!env.IsEnvironment("Test"))
        {
            services.AddMassTransit(mt =>
            {
                mt.SetKebabCaseEndpointNameFormatter();
                mt.AddConsumer<Haworks.Identity.Application.Consumers.PrivacyErasureRequestedConsumer>();
                mt.AddConsumer<Haworks.Identity.Application.Consumers.JwtKeyRotatedConsumer>();
                mt.UsingRabbitMq((context, cfg) =>
                {
                    var rabbitConn = configuration.GetConnectionString("rabbitmq")
                        ?? throw new InvalidOperationException(
                            "ConnectionStrings:rabbitmq is missing. Aspire injects it via WithReference(rabbitmq).");
                    cfg.Host(new Uri(rabbitConn));
                    cfg.ConfigureStandardRabbitMq(context);
                });
            });
            services.AddDomainEventPublisher();
        }

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
