using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Microsoft.Extensions.Logging;
using Moq;
using VaultSharp;
using VaultSharp.V1;
using VaultSharp.V1.SecretsEngines;
using VaultSharp.V1.SecretsEngines.Database;
using Xunit;

namespace Haworks.BuildingBlocks.Unit.Vault;

public class VaultCredentialProviderConcurrencyTests
{
    private readonly Mock<IVaultClient> _vaultClient = new();
    private readonly Mock<IDatabaseSecretsEngine> _dbEngine = new();
    private readonly Mock<ILogger<VaultCredentialProvider>> _logger = new();

    public VaultCredentialProviderConcurrencyTests()
    {
        var v1 = new Mock<IVaultClientV1>();
        var secrets = new Mock<ISecretsEngine>();
        _vaultClient.Setup(x => x.V1).Returns(v1.Object);
        v1.Setup(x => x.Secrets).Returns(secrets.Object);
        secrets.Setup(x => x.Database).Returns(_dbEngine.Object);
    }

    [Fact]
    public async Task ConcurrentCalls_OnlyOneFetchesFromVault()
    {
        var tcs = new TaskCompletionSource<bool>();

        _dbEngine.Setup(x => x.GetStaticCredentialsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (string _, string __, string ___) =>
            {
                // Simulate slow Vault call so all 20 tasks queue up
                await tcs.Task;
                var data = new StaticCredentials { Username = "user1", Password = "pass1" };
                return new VaultSharp.V1.Commons.Secret<StaticCredentials> { Data = data };
            });

        using var provider = new VaultCredentialProvider(
            _vaultClient.Object, _logger.Object, TimeSpan.FromHours(1));

        // Launch 20 concurrent calls with expired cache (no cache exists yet)
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => provider.GetDatabaseCredentialsAsync("test-role"))
            .ToArray();

        // Unblock the Vault call
        tcs.SetResult(true);

        var results = await Task.WhenAll(tasks);

        // SemaphoreSlim should ensure only one actual Vault call
        _dbEngine.Verify(x => x.GetStaticCredentialsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        results.Should().HaveCount(20);
    }

    [Fact]
    public async Task ConcurrentCalls_AllReturnSameResult()
    {
        var tcs = new TaskCompletionSource<bool>();

        _dbEngine.Setup(x => x.GetStaticCredentialsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (string _, string __, string ___) =>
            {
                await tcs.Task;
                var data = new StaticCredentials { Username = "shared-user", Password = "shared-pass" };
                return new VaultSharp.V1.Commons.Secret<StaticCredentials> { Data = data };
            });

        using var provider = new VaultCredentialProvider(
            _vaultClient.Object, _logger.Object, TimeSpan.FromHours(1));

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => provider.GetDatabaseCredentialsAsync("test-role"))
            .ToArray();

        tcs.SetResult(true);
        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
        {
            r.Username.Should().Be("shared-user");
            r.Password.Should().Be("shared-pass");
        });
    }

    [Fact]
    public async Task FirstCallThrows_NoCache_PropagatesException()
    {
        _dbEngine.Setup(x => x.GetStaticCredentialsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Vault connection refused"));

        using var provider = new VaultCredentialProvider(
            _vaultClient.Object, _logger.Object, TimeSpan.FromHours(1));

        var act = () => provider.GetDatabaseCredentialsAsync("test-role");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Vault connection refused");
    }
}
