using System.Net.Http.Json;
using System.Text.Json;
using Polly;

namespace Haworks.BuildingBlocks.Vault;

/// <summary>
/// Hits POST /v1/auth/approle/login directly via HTTP and parses the response,
/// instead of going through VaultSharp's AppRole login provider.
///
/// VaultSharp 1.17 attaches the AppRole-issued token to subsequent KV reads
/// in a way Vault rejects with "permission denied" even when the policy
/// clearly allows it. A plain login + explicit TokenAuthMethodInfo on the
/// VaultClient round-trips correctly. Both startup config bootstrap and the
/// runtime VaultClientFactory share this implementation so the two paths
/// never drift.
/// </summary>
public sealed class VaultAppRoleAuthenticator : IVaultAppRoleAuthenticator
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<VaultAppRoleAuthenticator>? _logger;

    public VaultAppRoleAuthenticator(
        IHttpClientFactory? httpClientFactory = null,
        ILogger<VaultAppRoleAuthenticator>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<VaultAppRoleLoginResult> LoginAsync(
        string vaultAddress,
        string roleId,
        string secretId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vaultAddress))
            throw new ArgumentException("Vault address required.", nameof(vaultAddress));
        if (string.IsNullOrWhiteSpace(roleId))
            throw new ArgumentException("Role ID required.", nameof(roleId));
        if (string.IsNullOrWhiteSpace(secretId))
            throw new ArgumentException("Secret ID required.", nameof(secretId));

        // At bootstrap (before DI is built), IHttpClientFactory is unavailable.
        var factoryClient = _httpClientFactory?.CreateClient(nameof(VaultAppRoleAuthenticator));
        using var fallbackClient = factoryClient == null ? new HttpClient() : null;
        var http = factoryClient ?? fallbackClient!;
        {
            http.BaseAddress ??= new Uri(vaultAddress);

            var policy = Policy
                .Handle<HttpRequestException>()
                .Or<TaskCanceledException>()
                .WaitAndRetryAsync(
                    retryCount: 4,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (ex, ts, attempt, _) =>
                        _logger?.LogWarning(ex, "[VaultAuth] AppRole login attempt {Attempt} failed; retrying in {DelaySeconds}s", attempt, ts.TotalSeconds));

            var resp = await policy.ExecuteAsync(async () =>
            {
                var r = await http.PostAsJsonAsync(
                    "/v1/auth/approle/login",
                    new { role_id = roleId, secret_id = secretId },
                    cancellationToken).ConfigureAwait(false);
                r.EnsureSuccessStatusCode();
                return r;
            }).ConfigureAwait(false);

            var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var auth = doc.RootElement.GetProperty("auth");
            var token = auth.GetProperty("client_token").GetString()
                ?? throw new InvalidOperationException("Vault AppRole login response missing auth.client_token.");

            var leaseSeconds = auth.TryGetProperty("lease_duration", out var leaseEl)
                ? leaseEl.GetInt64()
                : 3600L;
            var leaseDuration = TimeSpan.FromSeconds(leaseSeconds);

            _logger?.LogInformation("[VaultAuth] AppRole login succeeded; token lease_duration={LeaseSeconds}s", leaseSeconds);
            return new VaultAppRoleLoginResult(token, leaseDuration);
        }
    }
}
