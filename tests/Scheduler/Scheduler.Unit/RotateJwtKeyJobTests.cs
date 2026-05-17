using FluentAssertions;
using Haworks.Contracts.Secrets;
using Haworks.Scheduler.Application.Jobs;
using Hangfire;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using System.Linq.Expressions;
using VaultSharp;
using VaultSharp.V1;
using VaultSharp.V1.Commons;
using VaultSharp.V1.SecretsEngines;
using VaultSharp.V1.SecretsEngines.KeyValue;
using VaultSharp.V1.SecretsEngines.KeyValue.V2;

namespace Haworks.Scheduler.Unit;

public class RotateJwtKeyJobTests
{
    private readonly Mock<IVaultClient> _vaultClient;
    private readonly Mock<IPublishEndpoint> _publishEndpoint;
    private readonly Mock<IBackgroundJobClient> _backgroundJobClient;
    private readonly Mock<ILogger<RotateJwtKeyJob>> _logger;
    private readonly Mock<IKeyValueSecretsEngineV2> _kvV2;

    public RotateJwtKeyJobTests()
    {
        _vaultClient = new Mock<IVaultClient>();
        _publishEndpoint = new Mock<IPublishEndpoint>();
        _backgroundJobClient = new Mock<IBackgroundJobClient>();
        _logger = new Mock<ILogger<RotateJwtKeyJob>>();

        var v1 = new Mock<IVaultClientV1>();
        var secrets = new Mock<ISecretsEngine>();
        var kv = new Mock<IKeyValueSecretsEngine>();
        _kvV2 = new Mock<IKeyValueSecretsEngineV2>();

        _vaultClient.Setup(x => x.V1).Returns(v1.Object);
        v1.Setup(x => x.Secrets).Returns(secrets.Object);
        secrets.Setup(x => x.KeyValue).Returns(kv.Object);
        kv.Setup(x => x.V2).Returns(_kvV2.Object);
    }

    [Fact]
    public async Task Writes_new_key_and_preserves_previous()
    {
        // Arrange
        SetupCurrentKey("existing-pem-key");

        var job = CreateJob();

        // Act
        await job.RunAsync(CancellationToken.None);

        // Assert: writes to jwt-previous (preserving old key)
        _kvV2.Verify(x => x.WriteSecretAsync(
            "identity/jwt-previous",
            It.Is<Dictionary<string, object>>(d => d.ContainsKey("signing_key") && (string)d["signing_key"] == "existing-pem-key"),
            It.IsAny<int?>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);

        // Assert: writes new key to identity/jwt
        _kvV2.Verify(x => x.WriteSecretAsync(
            "identity/jwt",
            It.Is<Dictionary<string, object>>(d => d.ContainsKey("signing_key") && (string)d["signing_key"] != "existing-pem-key"),
            It.IsAny<int?>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Publishes_JwtKeyRotatedEvent()
    {
        SetupCurrentKey("some-key");
        var job = CreateJob();

        await job.RunAsync(CancellationToken.None);

        _publishEndpoint.Verify(x => x.Publish(
            It.Is<JwtKeyRotatedEvent>(e => e.RotationId != Guid.Empty && e.RotatedAt <= DateTimeOffset.UtcNow),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Schedules_ClearPreviousJwtKeyJob_in_15_minutes()
    {
        SetupCurrentKey("some-key");
        var job = CreateJob();

        await job.RunAsync(CancellationToken.None);

        _backgroundJobClient.Verify(x => x.Create(
            It.IsAny<Hangfire.Common.Job>(),
            It.Is<Hangfire.States.IState>(s => s is Hangfire.States.ScheduledState scheduled
                && scheduled.ScheduledAt >= DateTimeOffset.UtcNow.AddMinutes(14)
                && scheduled.ScheduledAt <= DateTimeOffset.UtcNow.AddMinutes(16))),
            Times.Once);
    }

    private void SetupCurrentKey(string keyValue)
    {
        var secretData = new Dictionary<string, object> { ["signing_key"] = keyValue };
        var data = new SecretData { Data = secretData };
        var secret = new Secret<SecretData> { Data = data };

        _kvV2.Setup(x => x.ReadSecretAsync(
                "identity/jwt", It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(secret);

        _kvV2.Setup(x => x.WriteSecretAsync(
                It.IsAny<string>(), It.IsAny<IDictionary<string, object>>(),
                It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.FromResult(new Secret<CurrentSecretMetadata> { Data = new CurrentSecretMetadata() }));
    }

    private RotateJwtKeyJob CreateJob() =>
        new(_vaultClient.Object, _publishEndpoint.Object, _backgroundJobClient.Object, _logger.Object);
}
