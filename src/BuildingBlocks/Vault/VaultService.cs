using System.Collections.Concurrent;
using System.Security;
using Haworks.BuildingBlocks.Telemetry;
using Haworks.BuildingBlocks.Vault.Options;
using Haworks.BuildingBlocks.Resilience;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using VaultSharp;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Orchestrates Vault operations for database credential management.
///
/// Maintains a credential store per Vault role (e.g.
/// <c>haworks-catalog</c>, <c>haworks-orders</c>, ...). Each store is
/// independently refreshed on demand or by the background renewal loop. Per-
/// bounded-context callers (the EF connection interceptor for that context)
/// pass their own role name in.
///
/// Uses circuit breaker and retry policies for resilience.
/// </summary>
public class VaultService : IVaultService
{
    private readonly ILogger<VaultService> _logger;
    private readonly ITelemetryService _telemetry;
    private readonly VaultOptions _vaultOptions;
    private readonly DatabaseOptions _dbOptions;
    private readonly IVaultClientFactory _clientFactory;
    private readonly IResiliencePolicyFactory _policyFactory;
    private readonly Func<ICredentialStore> _credentialStoreFactory;

    private readonly ConcurrentDictionary<string, ICredentialStore> _stores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _clientGate = new(1, 1);

    // Refresh the AppRole-backed VaultClient this far ahead of token expiry.
    // Vault dev-mode default is 1h token TTL; 5 min headroom keeps us safe
    // even under clock skew and slow logins.
    private static readonly TimeSpan s_clientRefreshHeadroom = TimeSpan.FromMinutes(5);

    private IVaultClient? _client;
    private DateTime _clientExpiresAtUtc;
    private AsyncCircuitBreakerPolicy? _circuitBreaker;
    private AsyncRetryPolicy? _retryPolicy;
    private bool _initialized;
    private bool _disposed;

    public VaultService(
        IOptions<VaultOptions> vaultOpts,
        IOptions<DatabaseOptions> dbOpts,
        IVaultClientFactory clientFactory,
        IResiliencePolicyFactory policyFactory,
        Func<ICredentialStore> credentialStoreFactory,
        ILogger<VaultService> logger,
        ITelemetryService? telemetry = null)
    {
        _vaultOptions = vaultOpts.Value;
        _dbOptions = dbOpts.Value;
        _clientFactory = clientFactory;
        _policyFactory = policyFactory;
        _credentialStoreFactory = credentialStoreFactory;
        _logger = logger;
        _telemetry = telemetry ?? Haworks.BuildingBlocks.Telemetry.NullTelemetryService.Instance;

        ValidateConfiguration();
        BuildPolicies();
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_vaultOptions.Address))
            throw new ArgumentNullException(nameof(_vaultOptions.Address));
        if (string.IsNullOrWhiteSpace(_vaultOptions.RoleIdPath))
            throw new ArgumentNullException(nameof(_vaultOptions.RoleIdPath));
        if (string.IsNullOrWhiteSpace(_vaultOptions.SecretIdPath))
            throw new ArgumentNullException(nameof(_vaultOptions.SecretIdPath));
        if (string.IsNullOrWhiteSpace(_dbOptions.Host))
            throw new ArgumentNullException(nameof(_dbOptions.Host));
    }

    private void BuildPolicies()
    {
        var resilienceOptions = new ResilienceOptions
        {
            MaxRetryAttempts = _vaultOptions.MaxRetryAttempts,
            InitialRetryDelayMs = 200,
            CircuitBreakerThreshold = _vaultOptions.CircuitBreakerThreshold,
            CircuitBreakerDurationSeconds = _vaultOptions.CircuitBreakerDurationSeconds
        };

        _circuitBreaker = _policyFactory.CreateCircuitBreaker(resilienceOptions,
            onBreak: (ex, ts) =>
            {
                _logger.LogError(ex, "Circuit broken for {Duration}s", ts.TotalSeconds);
                _telemetry.TrackEvent("VaultCircuitBroken", new Dictionary<string, string>
                {
                    ["DurationSeconds"] = ts.TotalSeconds.ToString()
                });
            },
            onReset: () =>
            {
                _logger.LogInformation("Circuit reset");
                _telemetry.TrackEvent("VaultCircuitReset");
            });

        _retryPolicy = _policyFactory.CreateRetryPolicy(resilienceOptions,
            onRetry: (ex, ts, retryCount) =>
            {
                _logger.LogWarning(ex, "Retry {RetryCount} after {Seconds}s", retryCount, ts.TotalSeconds);
                _telemetry.TrackEvent("VaultRetry", new Dictionary<string, string>
                {
                    ["RetryCount"] = retryCount.ToString(),
                    ["DelaySeconds"] = ts.TotalSeconds.ToString()
                });
            });
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_initialized) return;

        _logger.LogInformation("Initializing VaultService...");
        var start = DateTime.UtcNow;

        await BuildClientAsync(ct);

        _initialized = true;
        _logger.LogInformation("Initialized in {ElapsedMs}ms", (DateTime.UtcNow - start).TotalMilliseconds);
        _telemetry.TrackEvent("VaultServiceInitialized", new Dictionary<string, string>
        {
            ["ElapsedMs"] = (DateTime.UtcNow - start).TotalMilliseconds.ToString()
        });
    }

    /// <summary>
    /// Returns a VaultClient whose AppRole token is still valid for at least
    /// <see cref="s_clientRefreshHeadroom"/>. Rebuilds (re-authenticates) when
    /// the cached client is past that threshold.
    ///
    /// Concurrent callers serialise on <see cref="_clientGate"/> so a token
    /// near expiry only triggers one re-login. This replaces VaultSharp's
    /// AppRoleAuthMethodInfo lazy auth path, which we can't use here because
    /// of the KV-read permission-denied bug documented in
    /// <see cref="VaultAppRoleAuthenticator"/>.
    /// </summary>
    private async Task<IVaultClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null && DateTime.UtcNow + s_clientRefreshHeadroom < _clientExpiresAtUtc)
            return _client;

        await _clientGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null && DateTime.UtcNow + s_clientRefreshHeadroom < _clientExpiresAtUtc)
                return _client;

            _logger.LogInformation("Vault AppRole token nearing expiry (expiresAt={ExpiresAt:O}); re-authenticating.", _clientExpiresAtUtc);
            await BuildClientAsync(ct);
            return _client!;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private async Task BuildClientAsync(CancellationToken ct)
    {
        var handle = await _clientFactory.CreateClientAsync(_vaultOptions, ct);
        (_client as IDisposable)?.Dispose();
        _client = handle.Client;
        _clientExpiresAtUtc = handle.CreatedAt + handle.LeaseDuration;
        _logger.LogInformation("Vault AppRole token issued; lease={LeaseMinutes:F1} min, expiresAt={ExpiresAt:O}",
            handle.LeaseDuration.TotalMinutes, _clientExpiresAtUtc);
    }

    public DateTime LeaseExpiryFor(string roleName) =>
        _stores.TryGetValue(roleName, out var store) ? store.LeaseExpiry : DateTime.MinValue;

    public TimeSpan LeaseDurationFor(string roleName) =>
        _stores.TryGetValue(roleName, out var store) ? store.LeaseDuration : TimeSpan.Zero;

    public async Task<(string Username, SecureString Password)> GetDatabaseCredentialsAsync(string roleName, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureInitialized();
        ValidateRoleName(roleName);

        var store = _stores.GetOrAdd(roleName, _ => _credentialStoreFactory());

        var refreshThreshold = TimeSpan.FromMinutes(5);
        if (!store.IsExpiredOrNearExpiry(refreshThreshold))
            return store.Current;

        await RefreshCredentialsInternal(roleName, store, ct);
        return store.Current;
    }

    public async Task RefreshCredentials(string roleName, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureInitialized();
        ValidateRoleName(roleName);

        var store = _stores.GetOrAdd(roleName, _ => _credentialStoreFactory());
        var combined = Policy.WrapAsync(_circuitBreaker!, _retryPolicy!);
        await combined.ExecuteAsync(token => RefreshCredentialsInternal(roleName, store, token), ct);
    }

    private async Task RefreshCredentialsInternal(string roleName, ICredentialStore store, CancellationToken ct)
    {
        _logger.LogInformation("Refreshing credentials for role {Role}", roleName);
        var start = DateTime.UtcNow;

        await store.RefreshAsync(async innerCt =>
        {
            var client = await GetClientAsync(innerCt);
            var resp = await client.V1.Secrets.Database.GetCredentialsAsync(roleName);
            var securePwd = new SecureString();
            foreach (var c in resp.Data.Password)
                securePwd.AppendChar(c);
            securePwd.MakeReadOnly();
            return (resp.Data.Username, securePwd, TimeSpan.FromSeconds(resp.LeaseDurationSeconds));
        }, ct);

        _logger.LogInformation("Credentials refreshed for {Role}. Expires at {Expiry}", roleName, store.LeaseExpiry);
        _telemetry.TrackEvent("VaultCredentialsRefreshed", new Dictionary<string, string>
        {
            ["Role"] = roleName,
            ["ElapsedMs"] = (DateTime.UtcNow - start).TotalMilliseconds.ToString(),
            ["LeaseExpiry"] = store.LeaseExpiry.ToString("O")
        });
    }

    public async Task<string> GetDatabaseConnectionStringAsync(string roleName, CancellationToken ct = default)
    {
        var (user, pwd) = await GetDatabaseCredentialsAsync(roleName, ct);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = _dbOptions.Host,
            Port = _dbOptions.Port,
            Database = _dbOptions.Database,
            Username = user,
            Password = pwd.ToInsecureString(),
            SslMode = Enum.TryParse<SslMode>(_dbOptions.SslMode, ignoreCase: true, out var sm) ? sm : SslMode.Disable,
            MaxPoolSize = 50
        };
        return builder.ConnectionString;
    }

    public async Task<string?> GetKvSecretAsync(string path, string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            var combined = Policy.WrapAsync(_circuitBreaker!, _retryPolicy!);
            return await combined.ExecuteAsync(async innerCt =>
            {
                var client = await GetClientAsync(innerCt);
                var mountPoint = _vaultOptions.KvMountPoint ?? "secret";
                var secret = await client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
                    path: path, mountPoint: mountPoint);

                if (secret?.Data?.Data == null)
                {
                    _logger.LogWarning("Secret at path {Path} not found in Vault", path);
                    return null;
                }

                if (secret.Data.Data.TryGetValue(key, out var value))
                {
                    _logger.LogDebug("Successfully retrieved secret {Key} from path {Path}", key, path);
                    return value?.ToString();
                }

                _logger.LogWarning("Key {Key} not found in secret at path {Path}", key, path);
                return null;
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve secret {Key} from path {Path}", key, path);
            _telemetry.TrackException(ex);
            return null;
        }
    }

    public async Task StartCredentialRenewalAsync(CancellationToken stoppingToken)
    {
        EnsureInitialized();
        _logger.LogInformation("Starting renewal loop.");

        var jitterRng = new Random();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Refresh every cached store. The dictionary may be modified
                // by other code paths concurrently (new role on demand) so
                // snapshot the keys before iterating.
                foreach (var roleName in _stores.Keys.ToArray())
                {
                    await RefreshCredentials(roleName, stoppingToken);
                }

                // Sleep until the SOONEST-expiring store needs refresh, with
                // a 5-min safety lead and 0-30s jitter. If no roles are
                // cached yet, poll every minute to wake up when one appears.
                TimeSpan delay;
                if (_stores.IsEmpty)
                {
                    delay = TimeSpan.FromMinutes(1);
                }
                else
                {
                    var earliestExpiry = _stores.Values.Min(s => s.LeaseExpiry);
                    delay = (earliestExpiry - TimeSpan.FromMinutes(5)) - DateTime.UtcNow;
                    if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);
                }

                var jitterMs = jitterRng.Next(0, 30000);
                delay = delay.Add(TimeSpan.FromMilliseconds(jitterMs));

                _logger.LogDebug("Next credential refresh in {DelayMinutes:F1} minutes (includes {JitterMs}ms jitter)",
                    delay.TotalMinutes, jitterMs);

                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Renewal error, retrying in 1 minute.");
                _telemetry.TrackException(ex);

                var errorJitterMs = jitterRng.Next(0, 10000);
                await Task.Delay(TimeSpan.FromMinutes(1).Add(TimeSpan.FromMilliseconds(errorJitterMs)), stoppingToken);
            }
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync first.");
    }

    private static void ValidateRoleName(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new ArgumentException("Vault database role name is required.", nameof(roleName));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VaultService));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            foreach (var store in _stores.Values)
            {
                store.Current.Password?.Dispose();
            }
            (_client as IDisposable)?.Dispose();
            _clientGate.Dispose();
        }
        _disposed = true;
    }


    public Task<VaultTokenInfo> GetTokenInfoAsync(CancellationToken ct = default)
    {
        if (!_initialized || _client == null)
            return Task.FromResult(new VaultTokenInfo(0, string.Empty, false));

        var ttlSeconds = (int)Math.Max(0, (_clientExpiresAtUtc - DateTime.UtcNow).TotalSeconds);
        // The underlying VaultSharp client hides the actual lease ID of the auth token.
        // We synthesize a lease ID string from the address for demo purposes.
        return Task.FromResult(new VaultTokenInfo(ttlSeconds, $"{_vaultOptions.Address}_token", true));
    }

    public async Task RevokeTokenAsync(CancellationToken ct = default)
    {
        // Safe no-op if no client built yet — nothing to revoke.
        if (_client is null) return;

        try
        {
            await _client.V1.Auth.Token.RevokeSelfAsync().ConfigureAwait(false);
            _logger.LogInformation("[VaultService] Token revoked via auth/token/revoke-self");
        }
        catch (Exception ex)
        {
            // Shutdown is in flight — nothing useful to do on failure.
            // Token will expire naturally at its TTL anyway. Log + swallow.
            _logger.LogWarning(ex,
                "[VaultService] Failed to revoke token on shutdown; relying on natural TTL expiry");
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
