using Microsoft.Extensions.Logging;
using VaultSharp;
using VaultSharp.Core;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Fetches database credentials from Vault static roles with in-memory caching.
/// Thread-safe: uses SemaphoreSlim to guard cache updates.
/// </summary>
public sealed class VaultCredentialProvider : IVaultCredentialProvider, IDisposable
{
    private readonly IVaultClient _vaultClient;
    private readonly ILogger<VaultCredentialProvider> _logger;
    private readonly TimeSpan _cacheExpiry;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private (string Username, string Password)? _cached;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <param name="vaultClient">VaultSharp client instance.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="rotationPeriod">The Vault static role rotation period. Credentials are cached for 90% of this value.</param>
    public VaultCredentialProvider(
        IVaultClient vaultClient,
        ILogger<VaultCredentialProvider> logger,
        TimeSpan? rotationPeriod = null)
    {
        _vaultClient = vaultClient;
        _logger = logger;
        _cacheExpiry = (rotationPeriod ?? TimeSpan.FromHours(1)) * 0.9;
    }

    public async Task<(string Username, string Password)> GetDatabaseCredentialsAsync(
        string roleName, CancellationToken ct = default)
    {
        if (_cached.HasValue && DateTimeOffset.UtcNow < _cachedUntil)
        {
            return _cached.Value;
        }

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_cached.HasValue && DateTimeOffset.UtcNow < _cachedUntil)
            {
                return _cached.Value;
            }

            try
            {
                var secret = await _vaultClient.V1.Secrets.Database
                    .GetStaticCredentialsAsync(roleName)
                    .ConfigureAwait(false);

                var username = secret.Data.Username;
                var password = secret.Data.Password;

                _cached = (username, password);
                _fetchedAt = DateTimeOffset.UtcNow;
                _cachedUntil = _fetchedAt.Add(_cacheExpiry);

                _logger.LogDebug(
                    "Fetched fresh credentials from Vault for role {RoleName}, cached until {CachedUntil:O}",
                    roleName, _cachedUntil);

                return (username, password);
            }
            catch (VaultApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                // Vault sealed or unavailable — return last-known-good
                if (_cached.HasValue)
                {
                    _logger.LogWarning(
                        "Vault unavailable (503) for role {RoleName}; returning stale cached credentials",
                        roleName);
                    return _cached.Value;
                }

                throw;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public VaultLeaseStatus GetLeaseStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var hasCredentials = _cached.HasValue;

        if (!hasCredentials)
        {
            return new VaultLeaseStatus
            {
                CachedUntil = DateTimeOffset.MinValue,
                FetchedAt = DateTimeOffset.MinValue,
                TtlPercentElapsed = 0.0,
                HasCredentials = false
            };
        }

        var totalTtlSeconds = _cacheExpiry.TotalSeconds;
        var elapsedSeconds = (now - _fetchedAt).TotalSeconds;
        var percentElapsed = totalTtlSeconds > 0 ? elapsedSeconds / totalTtlSeconds : 1.0;

        return new VaultLeaseStatus
        {
            CachedUntil = _cachedUntil,
            FetchedAt = _fetchedAt,
            TtlPercentElapsed = percentElapsed,
            HasCredentials = true
        };
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
