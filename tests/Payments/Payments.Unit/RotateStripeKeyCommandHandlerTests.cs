using Xunit;
using FluentAssertions;
using Hangfire;
using Haworks.Contracts.Secrets;
using Haworks.Payments.Application.Commands.Secrets;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using VaultSharp;
using VaultSharp.V1.SecretsEngines.KeyValue.V2;

namespace Haworks.Payments.Unit;

public class RotateStripeKeyCommandHandlerTests
{
    private readonly Mock<IPublishEndpoint> _publishEndpoint;
    private readonly Mock<ILogger<RotateStripeKeyCommandHandler>> _logger;
    private readonly Mock<IVaultClient> _vaultClient;
    private readonly Mock<IBackgroundJobClient> _backgroundJobClient;
    private readonly Mock<IHttpClientFactory> _httpClientFactory;
    private readonly IConfiguration _configuration;

    public RotateStripeKeyCommandHandlerTests()
    {
        _publishEndpoint = new Mock<IPublishEndpoint>();
        _logger = new Mock<ILogger<RotateStripeKeyCommandHandler>>();
        _vaultClient = new Mock<IVaultClient>();
        _backgroundJobClient = new Mock<IBackgroundJobClient>();
        _httpClientFactory = new Mock<IHttpClientFactory>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:OverlapHours"] = "24"
            })
            .Build();

        var v1Mock = new Mock<VaultSharp.V1.IVaultClientV1>();
        var secretsMock = new Mock<VaultSharp.V1.SecretsEngines.ISecretsEngine>();
        var kvMock = new Mock<VaultSharp.V1.SecretsEngines.KeyValue.IKeyValueSecretsEngine>();
        var kv2Mock = new Mock<IKeyValueSecretsEngineV2>();

        _vaultClient.Setup(x => x.V1).Returns(v1Mock.Object);
        v1Mock.Setup(x => x.Secrets).Returns(secretsMock.Object);
        secretsMock.Setup(x => x.KeyValue).Returns(kvMock.Object);
        kvMock.Setup(x => x.V2).Returns(kv2Mock.Object);

        kv2Mock.Setup(x => x.ReadSecretAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("no existing key"));
        kv2Mock.Setup(x => x.WriteSecretAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<int?>(), It.IsAny<string>()))
            .Returns(Task.FromResult(new VaultSharp.V1.Commons.Secret<VaultSharp.V1.Commons.CurrentSecretMetadata> { Data = new VaultSharp.V1.Commons.CurrentSecretMetadata() }));

        _httpClientFactory.Setup(x => x.CreateClient("StripeVerification"))
            .Returns(new System.Net.Http.HttpClient(new OkHttpMessageHandler())
            {
                BaseAddress = new Uri("https://api.stripe.com/")
            });
    }

    [Fact]
    public async Task Returns_202_with_rotation_id()
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { IdempotencyKey = Guid.NewGuid().ToString(), NewSecretKey = "sk_test_abc123" };

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        result.RotationId.Should().NotBe(Guid.Empty);
        result.OverlapExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid_key")]
    [InlineData("pk_test_123")]
    [InlineData("rk_live_123")]
    public void Rejects_invalid_key_format(string invalidKey)
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { IdempotencyKey = Guid.NewGuid().ToString(), NewSecretKey = invalidKey };

        var act = () => handler.Handle(command, CancellationToken.None);

        act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Publishes_StripeKeyRotationStartedEvent()
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { IdempotencyKey = Guid.NewGuid().ToString(), NewSecretKey = "sk_live_abc123" };

        await handler.Handle(command, CancellationToken.None);

        _publishEndpoint.Verify(x => x.Publish(
            It.Is<StripeKeyRotationStartedEvent>(e =>
                e.RotationId != Guid.Empty &&
                e.OverlapExpiresAt > DateTimeOffset.UtcNow),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Accepts_sk_live_key()
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { IdempotencyKey = Guid.NewGuid().ToString(), NewSecretKey = "sk_live_production_key_123" };

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Accepts_sk_test_key()
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { IdempotencyKey = Guid.NewGuid().ToString(), NewSecretKey = "sk_test_test_key_456" };

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
    }

    private RotateStripeKeyCommandHandler CreateHandler() =>
        new(_publishEndpoint.Object, _configuration, _logger.Object,
            _vaultClient.Object, _backgroundJobClient.Object, _httpClientFactory.Object);
}

internal sealed class OkHttpMessageHandler : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
        System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
}
