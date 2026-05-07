using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Haworks.Payments.Infrastructure.Stripe;

/// <summary>
/// Thread-safe factory for Stripe clients with automatic caching.
/// Prevents thread starvation during key rotation or cold starts.
/// </summary>
public sealed class StripeClientFactory : IStripeClientFactory
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<PaymentProviderOptions> _options;
    private readonly ILogger<StripeClientFactory> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    private IStripeClient? _cachedClient;
    private DateTime _clientExpiry = DateTime.MinValue;
    private static readonly TimeSpan ClientCacheDuration = TimeSpan.FromMinutes(30);

    public StripeClientFactory(
        IConfiguration configuration,
        IOptions<PaymentProviderOptions> options,
        ILogger<StripeClientFactory> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IStripeClient> GetClientAsync(CancellationToken ct = default)
    {
        // Fast path: check if cached client is still valid
        if (_cachedClient != null && DateTime.UtcNow < _clientExpiry)
        {
            return _cachedClient;
        }

        await _clientLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedClient != null && DateTime.UtcNow < _clientExpiry)
            {
                return _cachedClient;
            }

            // Bind Stripe options to get keys. Configuration picks up from Vault (local) or Env (prod).
            var stripeSecretKey = _configuration["Stripe:SecretKey"] 
                                  ?? _configuration["Payments:Stripe:SecretKey"]
                                  ?? _options.Value.Stripe.SecretKey;
            
            if (string.IsNullOrEmpty(stripeSecretKey))
            {
                throw new InvalidOperationException("Stripe:SecretKey is not configured.");
            }
            
            var baseUrl = _options.Value?.Stripe?.BaseUrl;
            
            if (!string.IsNullOrEmpty(baseUrl))
            {
                _logger.LogInformation("Creating StripeClient with custom base URL: {BaseUrl}", baseUrl);
                _cachedClient = new StripeClient(stripeSecretKey, apiBase: baseUrl);
            }
            else
            {
                _cachedClient = new StripeClient(stripeSecretKey);
            }

            _clientExpiry = DateTime.UtcNow.Add(ClientCacheDuration);
            _logger.LogDebug("Created new StripeClient, expires at {Expiry}", _clientExpiry);

            return _cachedClient;
        }
        finally
        {
            _clientLock.Release();
        }
    }
}
