using FluentAssertions;
using Haworks.Contracts.Secrets;
using Haworks.Scheduler.Application.Jobs;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using VaultSharp;
using VaultSharp.Core;
using VaultSharp.V1;
using VaultSharp.V1.SecretsEngines;
using VaultSharp.V1.SecretsEngines.KeyValue;
using VaultSharp.V1.SecretsEngines.KeyValue.V2;

namespace Haworks.Scheduler.Unit;

public class SecretExpiryWatcherJobTests
{
    private readonly Mock<IVaultClient> _vaultClient;
    private readonly Mock<IPublishEndpoint> _publishEndpoint;
    private readonly Mock<ILogger<SecretExpiryWatcherJob>> _logger;
    private readonly Mock<IVaultClientV1> _v1;
    private readonly Mock<ISecretsEngine> _secretsEngine;
    private readonly Mock<IKeyValueSecretsEngine> _kvEngine;
    private readonly Mock<IKeyValueSecretsEngineV2> _kvV2;

    public SecretExpiryWatcherJobTests()
    {
        _vaultClient = new Mock<IVaultClient>();
        _publishEndpoint = new Mock<IPublishEndpoint>();
        _logger = new Mock<ILogger<SecretExpiryWatcherJob>>();
        _v1 = new Mock<IVaultClientV1>();
        _secretsEngine = new Mock<ISecretsEngine>();
        _kvEngine = new Mock<IKeyValueSecretsEngine>();
        _kvV2 = new Mock<IKeyValueSecretsEngineV2>();

        _vaultClient.Setup(x => x.V1).Returns(_v1.Object);
        _v1.Setup(x => x.Secrets).Returns(_secretsEngine.Object);
        _secretsEngine.Setup(x => x.KeyValue).Returns(_kvEngine.Object);
        _kvEngine.Setup(x => x.V2).Returns(_kvV2.Object);
    }

    [Fact]
    public async Task Publishes_warning_when_age_exceeds_threshold()
    {
        // Arrange: secret created 80 days ago (90-day TTL, 80% threshold = 72 days)
        var metadata = CreateMetadata(DateTimeOffset.UtcNow.AddDays(-80));
        _kvV2.Setup(x => x.ReadSecretMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(metadata);

        var job = CreateJob();

        // Act
        await job.RunAsync(CancellationToken.None);

        // Assert
        _publishEndpoint.Verify(x => x.Publish(
            It.Is<SecretExpiryWarningEvent>(e => e.AgePercent >= 0.80),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Does_not_publish_when_below_threshold()
    {
        // Arrange: secret created 10 days ago (90-day TTL, 80% threshold = 72 days)
        var metadata = CreateMetadata(DateTimeOffset.UtcNow.AddDays(-10));
        _kvV2.Setup(x => x.ReadSecretMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(metadata);

        var job = CreateJob();

        // Act
        await job.RunAsync(CancellationToken.None);

        // Assert
        _publishEndpoint.Verify(x => x.Publish(
            It.IsAny<SecretExpiryWarningEvent>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Skips_cycle_when_vault_returns_503()
    {
        // Arrange
        _kvV2.Setup(x => x.ReadSecretMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new VaultApiException(System.Net.HttpStatusCode.ServiceUnavailable, "sealed"));

        var job = CreateJob();

        // Act — should not throw
        var act = () => job.RunAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Assert: no events published
        _publishEndpoint.Verify(x => x.Publish(
            It.IsAny<SecretExpiryWarningEvent>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Skips_cycle_when_vault_sealed()
    {
        // Arrange
        _kvV2.Setup(x => x.ReadSecretMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new VaultApiException(System.Net.HttpStatusCode.InternalServerError, "Vault is sealed"));

        var job = CreateJob();

        // Act
        var act = () => job.RunAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Assert
        _publishEndpoint.Verify(x => x.Publish(
            It.IsAny<SecretExpiryWarningEvent>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private SecretExpiryWatcherJob CreateJob() =>
        new(_vaultClient.Object, _publishEndpoint.Object, _logger.Object);

    private static VaultSharp.V1.Commons.Secret<VaultSharp.V1.SecretsEngines.KeyValue.V2.SecretMetadata> CreateMetadata(DateTimeOffset createdTime)
    {
        var metadata = new VaultSharp.V1.SecretsEngines.KeyValue.V2.SecretMetadata
        {
            CreatedTime = createdTime
        };
        return new VaultSharp.V1.Commons.Secret<VaultSharp.V1.SecretsEngines.KeyValue.V2.SecretMetadata>
        {
            Data = metadata
        };
    }
}
