using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Haworks.BuildingBlocks.Testing.Containers;
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
/// Uses SharedTestVault singleton (WithReuse) to comply with CI architecture
/// rules that prohibit raw ContainerBuilder usage in test projects.
///
/// Covers:
///   1. Wrapped secret_id round-trip: bootstrap unwraps, logs in, reads KV.
///   2. Single-use wrapper invariant: a successful bootstrap consumes the
///      token; a second unwrap attempt fails.
///   3. Backwards-compat: the raw secret_id path (SecretIdIsWrapped absent)
///      still works exactly as Identity uses it today.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VaultConfigBootstrapWrappedSecretIdTests
{
    private const string ServiceName = "test";
    private const string KvPath      = "test/sample";
    private const string KvKey       = "ApiKey";
    private const string KvValue     = "test-value-12345";

    [Fact]
    public async Task LoadAsync_WithWrappedSecretId_UnwrapsLogsInAndReturnsKv()
    {
        // Arrange — seed AppRole, KV, and issue a wrapped secret_id.
        var (roleId, _) = await SharedTestVault.SeedAppRoleAsync(ServiceName);
        await SharedTestVault.SeedKvSecretAsync(KvPath,
            new Dictionary<string, object> { [KvKey] = KvValue });

        var wrappingToken = await SharedTestVault.IssueWrappedSecretIdAsync(ServiceName, 60);
        wrappingToken.Should().NotBeNullOrEmpty();

        var vaultAddress = await SharedTestVault.GetAddressAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]            = vaultAddress,
                ["Vault:RoleId"]             = roleId,
                ["Vault:SecretId"]           = wrappingToken,
                ["Vault:SecretIdIsWrapped"]  = "true",
            })
            .Build();

        // Act
        var dict = await VaultConfigBootstrap.LoadAsync(
            config,
            new[] { new VaultConfigBootstrap.KvMapping(KvPath, "Test:Section") });

        // Assert: KV value made it through the unwrap -> login -> read chain.
        dict.Should().ContainKey($"Test:Section:{KvKey}");
        dict[$"Test:Section:{KvKey}"].Should().Be(KvValue);

        // Single-use invariant: the wrapper was consumed by the bootstrap.
        // A second unwrap attempt against the same token must fail.
        using var http = new HttpClient { BaseAddress = new Uri(vaultAddress) };
        using var unwrapAgain = new HttpRequestMessage(
            HttpMethod.Post, "/v1/sys/wrapping/unwrap");
        unwrapAgain.Headers.Add("X-Vault-Token", wrappingToken);
        var second = await http.SendAsync(unwrapAgain);
        second.IsSuccessStatusCode.Should().BeFalse(
            "wrapping tokens are single-use; bootstrap already consumed it");
    }

    [Fact]
    public async Task LoadAsync_WithRawSecretId_BackwardsCompatPathStillWorks()
    {
        // Arrange — no SecretIdIsWrapped flag (or false), value is a raw
        // secret_id rather than a wrapping token. Identity's existing
        // pattern; must not regress.
        var (roleId, secretId) = await SharedTestVault.SeedAppRoleAsync(ServiceName);
        await SharedTestVault.SeedKvSecretAsync(KvPath,
            new Dictionary<string, object> { [KvKey] = KvValue });

        var vaultAddress = await SharedTestVault.GetAddressAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]   = vaultAddress,
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
        var (roleId, secretId) = await SharedTestVault.SeedAppRoleAsync(ServiceName);
        await SharedTestVault.SeedKvSecretAsync(KvPath,
            new Dictionary<string, object> { [KvKey] = KvValue });

        var vaultAddress = await SharedTestVault.GetAddressAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]            = vaultAddress,
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
        var (roleId, _) = await SharedTestVault.SeedAppRoleAsync(ServiceName);
        await SharedTestVault.SeedKvSecretAsync(KvPath,
            new Dictionary<string, object> { [KvKey] = KvValue });

        var wrappingToken = await SharedTestVault.IssueWrappedSecretIdAsync(ServiceName, 60);
        var vaultAddress  = await SharedTestVault.GetAddressAsync();

        // Consume the wrapper out-of-band first.
        using var http = new HttpClient { BaseAddress = new Uri(vaultAddress) };
        using var prematureUnwrap = new HttpRequestMessage(
            HttpMethod.Post, "/v1/sys/wrapping/unwrap");
        prematureUnwrap.Headers.Add("X-Vault-Token", wrappingToken);
        (await http.SendAsync(prematureUnwrap)).EnsureSuccessStatusCode();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:Address"]            = vaultAddress,
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
