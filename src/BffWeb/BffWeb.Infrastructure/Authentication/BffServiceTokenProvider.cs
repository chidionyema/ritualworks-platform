using System.IdentityModel.Tokens.Jwt;
using Haworks.BuildingBlocks.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Haworks.BffWeb.Infrastructure.Authentication;

public sealed class BffServiceTokenProvider : IServiceTokenProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BffServiceTokenProvider> _logger;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BffServiceTokenProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<BffServiceTokenProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-1))
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-1))
                return _cachedToken;

            var baseUrl = _configuration["Services:Identity:BaseUrl"];
            var secret = _configuration["ServiceAuth:SharedSecret"];

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(secret))
            {
                _logger.LogWarning("Service token not configured (Services:Identity:BaseUrl or ServiceAuth:SharedSecret missing)");
                return null;
            }

            var client = _httpClientFactory.CreateClient("IdentityServiceToken");
            client.BaseAddress = new Uri(baseUrl);

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Authentication/service-token");
            request.Headers.Add("X-Service-Secret", secret);

            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to obtain service token: {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            _cachedToken = doc.RootElement.GetProperty("token").GetString();

            if (_cachedToken != null)
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(_cachedToken);
                _tokenExpiry = jwt.ValidTo;
                _logger.LogInformation("Service token obtained, expires {Expiry}", _tokenExpiry);
            }

            return _cachedToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain service token");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }
}
