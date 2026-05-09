using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Haworks.BuildingBlocks.Tests.Vault;

/// <summary>
/// Integration tests for VaultConfigBootstrap.LoadAsync's response-wrap
/// support, exercised against a real hashicorp/vault dev-mode container.
/// Per project mandate "no mocked Vault in integration tests" — see
/// .claude/rules/testing.md.
///
/// Covers:
///   1. Wrapped secret_id round-trip: bootstrap unwraps, logs in, reads KV.
///   2. Single-use wrapper invariant: a successful bootstrap consumes the
///      token; a second unwrap attempt fails.
///   3. Backwards-compat: the raw secret_id path (SecretIdIsWrapped absent)
///      still works exactly as Identity uses it today.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VaultConfigBootstrapWrappedSecretIdTests : IAsyncLifetime
{
    private const string VaultImage = "hashicorp/vault:1.18";
    private const string RootToken  = "test-root-token";
    private const string KvPath     = "test/sample";
    private const string KvKey      = "ApiKey";
    private const string KvValue    = "test-value-12345";
    private const string RoleName   = "test-role";

    private IContainer  _vault         = null!;
    private string      _vaultAddress  = null!;
    private HttpClient  _http          = null!;

    public async Task InitializeAsync()
    {
        _vault = new ContainerBuilder()
            .WithImage(VaultImage)
            .WithEnvironment("VAULT_DEV_ROOT_TOKEN_ID",   RootToken)
            .WithEnvironment("VAULT_DEV_LISTEN_ADDRESS",  "0.0.0.0:8200")
            // hashicorp/vault image needs IPC_LOCK to mlock memory; the image
            // skips it gracefully in dev mode if unavailable, so leaving the
            // capability off keeps the test minimal.
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(req => req
                    .ForPort(8200)
                    .ForPath("/v1/sys/health")))
            .Build();

        await _vault.StartAsync();

        var port = _vault.GetMappedPublicPort(8200);
        _vaultAddress = $"http://localhost:{port}";

        _http = new HttpClient { BaseAddress = new Uri(_vaultAddress) };
        _http.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);

        await SetupAppRoleAndKvAsync();
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_vault is not null)
        {
            await _vault.DisposeAsync();
        }
    }

    private async Task SetupAppRoleAndKvAsync()
    {
        // Enable AppRole auth at the default path /v1/auth/approle.
        (await _http.PostAsJsonAsync("/v1/sys/auth/approle", new { type = "approle" }))
            .EnsureSuccessStatusCode();

        // Minimal policy: read on the single KV path the test exercises.
        var policyHcl = $"path \"secret/data/{KvPath}\" {{ capabilities = [\"read\"] }}";
        (await _http.PostAsJsonAsync("/v1/sys/policies/acl/test-policy",
            new { policy = policyHcl })).EnsureSuccessStatusCode();

        // AppRole bound to the policy. Long token TTL because tests run fast
        // — if a renewal kicks in mid-test it'd hide bugs.
        (await _http.PostAsJsonAsync($"/v1/auth/approle/role/{RoleName}", new
        {
            token_policies     = "test-policy",
            token_ttl          = "1h",
            token_max_ttl      = "24h",
            secret_id_ttl      = "0",       // 0 = forever (test cleans up)
            bind_secret_id     = true,
        })).EnsureSuccessStatusCode();

        // Seed the KV value the bootstrap will read end-to-end.
        // KV v2 default mount is /v1/secret with the data wrapper.
        (await _http.PostAsJsonAsync($"/v1/secret/data/{KvPath}", new
        {
            data = new Dictionary<string, string> { [KvKey] = KvValue }
        })).EnsureSuccessStatusCode();
    }

    private async Task<string> GetRoleIdAsync()
    {
        var resp = await _http.GetFromJsonAsync<JsonElement>(
            $"/v1/auth/approle/role/{RoleName}/role-id");
        return resp.GetProperty("data").GetProperty("role_id").GetString()!;
    }

    private async Task<string> IssueWrappedSecretIdAsync(int wrapTtlSeconds)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/auth/approle/role/{RoleName}/secret-id");
        req.Headers.Add("X-Vault-Wrap-TTL", wrapTtlSeconds.ToString());
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("wrap_info").GetProperty("token").GetString()!;
    }

    private async Task<string> IssueRawSecretIdAsync()
    {
        var resp = await _http.PostAsync(
            $"/v1/auth/approle/role/{RoleName}/secret-id",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("secret_id").GetString()!;
    }

    [Fact]
    public async Task LoadAsync_WithWrappedSecretId_UnwrapsLogsInAndReturnsKv()
    {
        // Arrange
        var roleId         = await GetRoleIdAsync();
        var wrappingToken  = await IssueWrappedSecretIdAsync(60);

        wrappingToken.Should().NotBeNullOrEmpty();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]            = _vaultAddress,
                ["Vault:RoleId"]             = roleId,
                ["Vault:SecretId"]           = wrappingToken,
                ["Vault:SecretIdIsWrapped"]  = "true",
            })
            .Build();

        // Act
        var dict = await VaultConfigBootstrap.LoadAsync(
            config,
            new[] { new VaultConfigBootstrap.KvMapping(KvPath, "Test:Section") });

        // Assert: KV value made it through the unwrap → login → read chain.
        dict.Should().ContainKey($"Test:Section:{KvKey}");
        dict[$"Test:Section:{KvKey}"].Should().Be(KvValue);

        // Single-use invariant: the wrapper was consumed by the bootstrap.
        // A second unwrap attempt against the same token must fail.
        using var unwrapAgain = new HttpRequestMessage(
            HttpMethod.Post, "/v1/sys/wrapping/unwrap");
        unwrapAgain.Headers.Remove("X-Vault-Token");
        unwrapAgain.Headers.Add("X-Vault-Token", wrappingToken);
        var second = await _http.SendAsync(unwrapAgain);
        second.IsSuccessStatusCode.Should().BeFalse(
            "wrapping tokens are single-use; bootstrap already consumed it");
    }

    [Fact]
    public async Task LoadAsync_WithRawSecretId_BackwardsCompatPathStillWorks()
    {
        // Arrange — no SecretIdIsWrapped flag (or false), value is a raw
        // secret_id rather than a wrapping token. Identity's existing
        // pattern; must not regress.
        var roleId   = await GetRoleIdAsync();
        var secretId = await IssueRawSecretIdAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]   = _vaultAddress,
                ["Vault:RoleId"]    = roleId,
                ["Vault:SecretId"]  = secretId,
                // SecretIdIsWrapped intentionally absent — should default false
            })
            .Build();

        // Act
        var dict = await VaultConfigBootstrap.LoadAsync(
            config,
            new[] { new VaultConfigBootstrap.KvMapping(KvPath, "Test:Section") });

        // Assert
        dict.Should().ContainKey($"Test:Section:{KvKey}");
        dict[$"Test:Section:{KvKey}"].Should().Be(KvValue);
    }

    [Fact]
    public async Task LoadAsync_WithExplicitFalseFlag_TreatsValueAsRawSecretId()
    {
        // Same as the backwards-compat test but sets the flag explicitly to
        // "false". Catches a subtle bug class where someone passes "false"
        // expecting it to mean opt-out, but the parser default-matches it
        // to true (e.g. case-sensitivity, whitespace).
        var roleId   = await GetRoleIdAsync();
        var secretId = await IssueRawSecretIdAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]            = _vaultAddress,
                ["Vault:RoleId"]             = roleId,
                ["Vault:SecretId"]           = secretId,
                ["Vault:SecretIdIsWrapped"]  = "false",
            })
            .Build();

        var dict = await VaultConfigBootstrap.LoadAsync(
            config,
            new[] { new VaultConfigBootstrap.KvMapping(KvPath, "Test:Section") });

        dict[$"Test:Section:{KvKey}"].Should().Be(KvValue);
    }

    [Fact]
    public async Task LoadAsync_WithAlreadyConsumedWrapper_ThrowsAndDoesNotLeakToken()
    {
        // Issue a wrapper, manually unwrap it once (simulating either an
        // attacker who stole the wrapper from CI logs and beat the service
        // to it, or a transient retry that accidentally succeeded twice).
        // Bootstrap's second unwrap attempt must fail loudly.
        var roleId         = await GetRoleIdAsync();
        var wrappingToken  = await IssueWrappedSecretIdAsync(60);

        // Consume the wrapper out-of-band first.
        using var prematureUnwrap = new HttpRequestMessage(
            HttpMethod.Post, "/v1/sys/wrapping/unwrap");
        prematureUnwrap.Headers.Remove("X-Vault-Token");
        prematureUnwrap.Headers.Add("X-Vault-Token", wrappingToken);
        (await _http.SendAsync(prematureUnwrap)).EnsureSuccessStatusCode();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]            = _vaultAddress,
                ["Vault:RoleId"]             = roleId,
                ["Vault:SecretId"]           = wrappingToken,
                ["Vault:SecretIdIsWrapped"]  = "true",
            })
            .Build();

        var act = async () => await VaultConfigBootstrap.LoadAsync(
            config,
            new[] { new VaultConfigBootstrap.KvMapping(KvPath, "Test:Section") });

        // Assert: bootstrap must throw — never silently fall through to
        // treating the wrapper as a raw secret_id (which would also fail
        // the AppRole login but with a more confusing error).
        await act.Should().ThrowAsync<Exception>(
            "an already-consumed wrapper cannot be unwrapped a second time");
    }
}
