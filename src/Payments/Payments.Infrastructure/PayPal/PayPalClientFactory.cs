using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Infrastructure.Options;
using Haworks.BuildingBlocks.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Haworks.Payments.Infrastructure.PayPal;

/// <summary>
/// Factory for creating authenticated PayPal HTTP clients.
/// Handles OAuth2 token acquisition with thread-safe caching.
/// </summary>
internal sealed class PayPalClientFactory : IPayPalClientFactory, IDisposable
{
    private readonly PayPalOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PayPalClientFactory> _logger;
    private readonly IAsyncPolicy _resiliencePolicy;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    /// <summary>
    /// Buffer time before token expiry to trigger refresh (5 minutes).
    /// </summary>
    private static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(5);

    /// <summary>
    /// HTTP client timeout for PayPal API calls.
    /// </summary>
    private static readonly TimeSpan HttpClientTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions TokenOptions = new() { PropertyNameCaseInsensitive = true };

    public string BaseUrl => _options.BaseUrl;

    public PayPalClientFactory(
        IOptions<PaymentProviderOptions> providerOptions,
        IHttpClientFactory httpClientFactory,
        IResiliencePolicyFactory resiliencePolicyFactory,
        ILogger<PayPalClientFactory> logger)
    {
        _options = providerOptions?.Value?.PayPal ?? throw new ArgumentNullException(nameof(providerOptions));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _resiliencePolicy = resiliencePolicyFactory.CreateCombinedPolicy(ResilienceOptions.PayPal);
    }

    /// <inheritdoc />
    public async Task<HttpClient> GetAuthenticatedClientAsync(CancellationToken ct = default)
    {
        // Fast path: check if token is still valid without lock
        if (IsTokenValid())
        {
            return CreateAuthenticatedClient(_accessToken!);
        }

        // Slow path: acquire lock and refresh token
        await _tokenLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock (another thread may have refreshed)
            if (IsTokenValid())
            {
                return CreateAuthenticatedClient(_accessToken!);
            }

            // Fetch new token with resilience
            var tokenResponse = await _resiliencePolicy.ExecuteAsync(
                async (ctx, token) => await FetchOAuthTokenAsync(token),
                new Context(),
                ct);

            _accessToken = tokenResponse.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            _logger.LogDebug(
                "PayPal OAuth token acquired, expires at {ExpiryTime}",
                _tokenExpiry);

            return CreateAuthenticatedClient(_accessToken);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool IsTokenValid()
    {
        return _accessToken != null &&
               DateTime.UtcNow < _tokenExpiry.Subtract(TokenExpiryBuffer);
    }

    private HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = _httpClientFactory.CreateClient("PayPal");
        client.BaseAddress = new Uri(_options.BaseUrl);
        client.Timeout = HttpClientTimeout;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    private async Task<PayPalTokenResponse> FetchOAuthTokenAsync(CancellationToken ct)
    {
        _logger.LogDebug("Fetching new PayPal OAuth token");

        var client = _httpClientFactory.CreateClient("PayPal");
        client.BaseAddress = new Uri(_options.BaseUrl);
        client.Timeout = HttpClientTimeout;

        // PayPal OAuth2 uses Basic auth with client credentials
        var authValue = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));

        var request = new HttpRequestMessage(HttpMethod.Post, PayPalEndpoints.OAuthToken)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await client.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "PayPal OAuth token request failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorBody);

            throw new HttpRequestException(
                $"PayPal OAuth token request failed: {response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var tokenResponse = JsonSerializer.Deserialize<PayPalTokenResponse>(
            responseBody,
            TokenOptions);

        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("PayPal OAuth response missing access_token");
        }

        _logger.LogInformation("PayPal OAuth token acquired successfully");
        return tokenResponse;
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
    }
}
