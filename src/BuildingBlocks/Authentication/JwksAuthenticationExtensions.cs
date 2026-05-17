using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Haworks.BuildingBlocks.Authentication;

/// <summary>
/// DI extensions that wire up JWT bearer authentication using a JWKS
/// endpoint (instead of a static signing key) for cluster-wide key
/// rotation.
/// </summary>
public static class JwksAuthenticationExtensions
{
    /// <summary>
    /// Registers JWT bearer authentication that resolves signing keys
    /// from a JWKS endpoint described by the
    /// <c>Authentication:Jwks</c> configuration section.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddJwksAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<JwksOptions>()
            .Bind(configuration.GetSection(JwksOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The ConfigurationManager is a long-lived, thread-safe object
        // that owns the JWKS HTTP cache. One instance per service is
        // exactly what we want — make it a singleton.
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<JwksOptions>>().Value;

            // OpenIdConnectConfigurationRetriever knows how to parse a
            // discovery document, but it also handles raw JWKS responses
            // (the keys are exposed via the SigningKeys collection).
            var env = sp.GetRequiredService<IHostEnvironment>();
            var manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                opts.JwksUri,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = !env.IsDevelopment() })
            {
                AutomaticRefreshInterval = opts.AutomaticRefresh
                    ? opts.RefreshInterval
                    // Disable scheduled refreshes by pinning to the max
                    // value the underlying field accepts.
                    : TimeSpan.FromDays(365),
            };

            return manager;
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, bearer =>
            {
                var jwksSection = configuration.GetSection(JwksOptions.SectionName);
                var issuer = jwksSection["Issuer"] ?? throw new InvalidOperationException("JwksOptions:Issuer required");
                var audience = jwksSection["Audience"] ?? throw new InvalidOperationException("JwksOptions:Audience required");

                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        // Post-configure: wire the IssuerSigningKeyResolver to use the ConfigurationManager.
        // This resolves keys lazily from the cached JWKS document (no sync-over-async,
        // no new HttpClient). The ConfigurationManager handles background refresh.
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(sp =>
            new PostConfigureJwksBearerOptions(
                sp.GetRequiredService<IConfigurationManager<OpenIdConnectConfiguration>>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("JwksKeyResolver")));

        // Pre-warm JWKS cache at startup so ResolveKeys never blocks
        services.AddHostedService<JwksWarmupHostedService>();

        return services;
    }

    private sealed class PostConfigureJwksBearerOptions(
        IConfigurationManager<OpenIdConnectConfiguration> manager,
        ILogger logger) : IPostConfigureOptions<JwtBearerOptions>
    {
        public void PostConfigure(string? name, JwtBearerOptions options)
        {
            options.TokenValidationParameters.IssuerSigningKeyResolver =
                (token, securityToken, kid, parameters) => ResolveKeys(kid);
        }

        private IEnumerable<SecurityKey> ResolveKeys(string? kid)
        {
            OpenIdConnectConfiguration? config;
            try
            {
                var task = manager.GetConfigurationAsync(CancellationToken.None);
#pragma warning disable HWK021 // IssuerSigningKeyResolver is sync by MS contract; cache-hit is non-blocking
                config = task.IsCompletedSuccessfully ? task.Result : null;
#pragma warning restore HWK021
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JWKS fetch failed; falling back to empty key set");
                config = null;
            }

            if (config is null)
                return Array.Empty<SecurityKey>();

            if (!string.IsNullOrEmpty(kid))
            {
                var matched = config.SigningKeys
                    .Where(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal))
                    .ToArray();
                if (matched.Length > 0) return matched;
            }

            return config.SigningKeys;
        }
    }

    /// <summary>
    /// Pre-warms the JWKS cache at startup so ResolveKeys never blocks on I/O.
    /// Runs as an IHostedService before the app accepts traffic.
    /// </summary>
    internal sealed class JwksWarmupHostedService(
        IConfigurationManager<OpenIdConnectConfiguration> manager,
        ILogger<JwksWarmupHostedService> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await manager.GetConfigurationAsync(cancellationToken);
                logger.LogInformation("JWKS cache pre-warmed successfully");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "JWKS pre-warm failed — keys will be fetched on first request");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
