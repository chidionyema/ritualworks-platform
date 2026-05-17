using FluentAssertions;
using Haworks.Contracts.Secrets;
using Haworks.Payments.Application.Commands.Secrets;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Haworks.Payments.Unit;

public class RotateStripeKeyCommandHandlerTests
{
    private readonly Mock<IPublishEndpoint> _publishEndpoint;
    private readonly Mock<ILogger<RotateStripeKeyCommandHandler>> _logger;
    private readonly IConfiguration _configuration;

    public RotateStripeKeyCommandHandlerTests()
    {
        _publishEndpoint = new Mock<IPublishEndpoint>();
        _logger = new Mock<ILogger<RotateStripeKeyCommandHandler>>();
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:OverlapHours"] = "24"
            })
            .Build();
    }

    [Fact]
    public async Task Returns_202_with_rotation_id()
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { NewSecretKey = "sk_test_abc123" };

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
    public async Task Rejects_invalid_key_format(string invalidKey)
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { NewSecretKey = invalidKey };

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Publishes_StripeKeyRotationStartedEvent()
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { NewSecretKey = "sk_live_abc123" };

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
        var command = new RotateStripeKeyCommand { NewSecretKey = "sk_live_production_key_123" };

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Accepts_sk_test_key()
    {
        var handler = CreateHandler();
        var command = new RotateStripeKeyCommand { NewSecretKey = "sk_test_test_key_456" };

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
    }

    private RotateStripeKeyCommandHandler CreateHandler() =>
        new(_publishEndpoint.Object, _configuration, _logger.Object);
}
