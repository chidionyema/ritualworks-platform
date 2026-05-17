using FluentAssertions;
using Haworks.BuildingBlocks.Vault;
using Microsoft.Extensions.Logging;
using Moq;
using VaultSharp;
using VaultSharp.Core;
using VaultSharp.V1;
using VaultSharp.V1.SecretsEngines;
using VaultSharp.V1.SecretsEngines.Database;

namespace Haworks.BuildingBlocks.Unit;

public class VaultCredentialProviderTests
{
    private readonly Mock<IVaultClient> _vaultClient;
    private readonly Mock<IDatabaseSecretsEngine> _dbEngine;
    private readonly Mock<ILogger<VaultCredentialProvider>> _logger;

    public VaultCredentialProviderTests()
    {
        _vaultClient = new Mock<IVaultClient>();
        _dbEngine = new Mock<IDatabaseSecretsEngine>();
        _logger = new Mock<ILogger<VaultCredentialProvider>>();

        var v1 = new Mock<IVaultClientV1>();
        var secrets = new Mock<ISecretsEngine>();
        _vaultClient.Setup(x => x.V1).Returns(v1.Object);
        v1.Setup(x => x.Secrets).Returns(secrets.Object);
        secrets.Setup(x => x.Database).Returns(_dbEngine.Object);
    }

    [Fact]
    public async Task Returns_cached_credentials_within_expiry()
    {
        // Arrange
        SetupVaultResponse("user1", "pass1");
        var provider = CreateProvider(rotationPeriod: TimeSpan.FromHours(1));

        // Act: first call populates cache
        await provider.GetDatabaseCredentialsAsync("test-role");
        // Second call should use cache (no additional Vault call)
        var result = await provider.GetDatabaseCredentialsAsync("test-role");

        // Assert
        result.Username.Should().Be("user1");
        result.Password.Should().Be("pass1");
        _dbEngine.Verify(x => x.GetStaticCredentialsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Fetches_new_credentials_after_expiry()
    {
        // Arrange: very short cache (expired immediately)
        SetupVaultResponse("user1", "pass1");
        var provider = CreateProvider(rotationPeriod: TimeSpan.FromMilliseconds(1));

        // Act
        await provider.GetDatabaseCredentialsAsync("test-role");
        await Task.Delay(10); // Wait for cache to expire
        SetupVaultResponse("user2", "pass2");
        var result = await provider.GetDatabaseCredentialsAsync("test-role");

        // Assert
        result.Username.Should().Be("user2");
        result.Password.Should().Be("pass2");
    }

    [Fact]
    public async Task Returns_stale_on_vault_503()
    {
        // Arrange: populate cache first
        SetupVaultResponse("cached-user", "cached-pass");
        var provider = CreateProvider(rotationPeriod: TimeSpan.FromMilliseconds(1));
        await provider.GetDatabaseCredentialsAsync("test-role");

        // Setup Vault to fail
        await Task.Delay(10);
        _dbEngine.Setup(x => x.GetStaticCredentialsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VaultApiException(System.Net.HttpStatusCode.ServiceUnavailable, "sealed"));

        // Act
        var result = await provider.GetDatabaseCredentialsAsync("test-role");

        // Assert: returns stale cached value
        result.Username.Should().Be("cached-user");
        result.Password.Should().Be("cached-pass");
    }

    private void SetupVaultResponse(string username, string password)
    {
        var credData = new StaticCredentials { Username = username, Password = password };
        var secret = new VaultSharp.V1.Commons.Secret<StaticCredentials> { Data = credData };

        _dbEngine.Setup(x => x.GetStaticCredentialsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(secret);
    }

    private VaultCredentialProvider CreateProvider(TimeSpan rotationPeriod) =>
        new(_vaultClient.Object, _logger.Object, rotationPeriod);
}
