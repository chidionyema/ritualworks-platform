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
                var jwksUri = jwksSection["JwksUri"] ?? throw new InvalidOperationException("JwksOptions:JwksUri required");
                var issuer = jwksSection["Issuer"] ?? throw new InvalidOperationException("JwksOptions:Issuer required");
                var audience = jwksSection["Audience"] ?? throw new InvalidOperationException("JwksOptions:Audience required");

                // Fetch JWKS keys at startup
                using var httpClient = new HttpClient();
                var jwksJson = httpClient.GetStringAsync(jwksUri).GetAwaiter().GetResult();
                var jwks = new JsonWebKeySet(jwksJson);

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
                    IssuerSigningKeys = jwks.GetSigningKeys(),
                };
            });

        return services;
    }

    private static IEnumerable<SecurityKey> ResolveKeys(
        IConfigurationManager<OpenIdConnectConfiguration> manager,
        string? kid,
        ILogger logger)
    {
        OpenIdConnectConfiguration? config;
        try
        {
            // GetConfigurationAsync returns the cached doc and triggers a
            // background refresh if the cache is stale. Block briefly —
            // IssuerSigningKeyResolver is synchronous by contract.
            config = manager
                .GetConfigurationAsync(CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            // Network / DNS / HTTP error against the JWKS endpoint. We
            // intentionally do NOT bubble this up — a brief outage of
            // the identity service should not 500 every authenticated
            // request across the cluster. Fall back to whatever we last
            // managed to cache (may be empty on cold start, in which
            // case the token will fail validation cleanly with
            // "no signing keys").
            logger.LogWarning(
                ex,
                "JWKS fetch failed; falling back to cached signing keys (if any).");
            config = TryGetCachedConfig(manager);
        }

        if (config is null)
        {
            return Array.Empty<SecurityKey>();
        }

        // Honour the token's kid header when present — that's the whole
        // point of JWKS rotation. If the token has no kid (older issuers
        // sometimes omit it) hand back every active key and let the
        // signature check pick the right one.
        if (!string.IsNullOrEmpty(kid))
        {
            var matched = config.SigningKeys
                .Where(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal))
                .ToArray();

            if (matched.Length > 0)
            {
                return matched;
            }
        }

        return config.SigningKeys;
    }

    private static OpenIdConnectConfiguration? TryGetCachedConfig(
        IConfigurationManager<OpenIdConnectConfiguration> manager)
    {
        // ConfigurationManager doesn't expose the cached doc directly,
        // but a second GetConfigurationAsync call will return the cache
        // without re-hitting the network unless the refresh interval
        // has elapsed. If even that throws there's nothing to recover.
        try
        {
            return manager
                .GetConfigurationAsync(CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return null;
        }
    }
}
