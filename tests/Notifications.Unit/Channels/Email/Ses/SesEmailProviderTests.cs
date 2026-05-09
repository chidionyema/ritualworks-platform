using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using FluentAssertions;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Infrastructure.Channels.Email.Ses;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Haworks.Notifications.Unit.Channels.Email.Ses;

public class SesEmailProviderTests
{
    private const string FromAddress = "noreply@haworks.test";
    private const string Recipient = "user@example.com";
    private const string Subject = "Hello";
    private const string Body = "<p>Hi there</p>";

    private static IOptions<SesOptions> BuildOptions() => Options.Create(new SesOptions
    {
        AccessKey = "AKIA-TEST",
        SecretKey = "SECRET-TEST",
        Region = "us-east-1",
        FromAddress = FromAddress
    });

    private static SesEmailProvider BuildSut(IAmazonSimpleEmailServiceV2 client) =>
        new(client, BuildOptions(), NullLogger<SesEmailProvider>.Instance);

    [Fact]
    public void Name_ReturnsAwsSesConstant()
    {
        var clientMock = new Mock<IAmazonSimpleEmailServiceV2>();
        var sut = BuildSut(clientMock.Object);

        sut.Name.Should().Be("aws-ses");
    }

    [Fact]
    public async Task SendAsync_OnHttpOk_ReturnsSuccessWithMessageId()
    {
        var clientMock = new Mock<IAmazonSimpleEmailServiceV2>();
        SendEmailRequest? captured = null;
        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendEmailRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new SendEmailResponse
            {
                MessageId = "ses-msg-123",
                HttpStatusCode = HttpStatusCode.OK
            });

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.IsRetryable.Should().BeFalse();
        result.ProviderMessageId.Should().Be("ses-msg-123");

        captured.Should().NotBeNull();
        captured!.FromEmailAddress.Should().Be(FromAddress);
        captured.Destination.ToAddresses.Should().ContainSingle().Which.Should().Be(Recipient);
        captured.Content.Simple.Subject.Data.Should().Be(Subject);
        captured.Content.Simple.Body.Html.Data.Should().Be(Body);
        captured.Content.Simple.Body.Text.Data.Should().Be(Body);
    }

    [Fact]
    public async Task SendAsync_WhenThrottled_ReturnsRetryable()
    {
        var clientMock = new Mock<IAmazonSimpleEmailServiceV2>();
        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TooManyRequestsException("Throttled"));

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Error.Should().Contain("throttled");
    }

    [Fact]
    public async Task SendAsync_WhenMessageRejected_ReturnsNonRetryable()
    {
        var clientMock = new Mock<IAmazonSimpleEmailServiceV2>();
        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MessageRejectedException("Email address is not verified"));

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Error.Should().Contain("rejected");
    }

    [Fact]
    public async Task SendAsync_OnGeneric4xxFromService_ReturnsNonRetryable()
    {
        var clientMock = new Mock<IAmazonSimpleEmailServiceV2>();
        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleEmailServiceV2Exception("ValidationError")
            {
                StatusCode = HttpStatusCode.BadRequest
            });

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Error.Should().Contain("validation");
    }

    [Fact]
    public async Task SendAsync_OnGeneric5xxFromService_ReturnsRetryable()
    {
        var clientMock = new Mock<IAmazonSimpleEmailServiceV2>();
        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleEmailServiceV2Exception("ServiceUnavailable")
            {
                StatusCode = HttpStatusCode.ServiceUnavailable
            });

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Error.Should().Contain("transient");
    }

    [Fact]
    public async Task SendAsync_OnEmptyRecipient_ReturnsNonRetryableImmediately()
    {
        var clientMock = new Mock<IAmazonSimpleEmailServiceV2>();
        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(string.Empty, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        clientMock.Verify(
            c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
