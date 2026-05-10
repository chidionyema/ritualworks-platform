using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Infrastructure.Channels.Email.SendGrid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SendGrid;
using SendGrid.Helpers.Mail;
using Xunit;

namespace Haworks.Notifications.Unit.Channels.Email.SendGrid;

public class SendGridEmailProviderTests
{
    private const string FromAddress = "noreply@haworks.test";
    private const string Recipient = "user@example.com";
    private const string Subject = "Hello";
    private const string Body = "<p>Hi there</p>";

    private static IOptions<SendGridOptions> BuildOptions() => Options.Create(new SendGridOptions
    {
        ApiKey = "SG.TEST-KEY",
        FromAddress = FromAddress
    });

    private static SendGridEmailProvider BuildSut(ISendGridClient client) =>
        new(client, BuildOptions(), NullLogger<SendGridEmailProvider>.Instance);

    [Fact]
    public void Name_ReturnsSendGridConstant()
    {
        var clientMock = new Mock<ISendGridClient>();
        var sut = BuildSut(clientMock.Object);

        sut.Name.Should().Be("sendgrid");
    }

    [Fact]
    public async Task SendAsync_On2xx_ReturnsSuccessWithMessageId()
    {
        var clientMock = new Mock<ISendGridClient>();
        SendGridMessage? captured = null;
        
        var responseMessage = new HttpResponseMessage();
        responseMessage.Headers.Add("X-Message-Id", "sg-msg-123");
        
        var response = new Response(HttpStatusCode.Accepted, null, responseMessage.Headers);

        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
            .Callback<SendGridMessage, CancellationToken>((msg, _) => captured = msg)
            .ReturnsAsync(response);

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.IsRetryable.Should().BeFalse();
        result.ProviderMessageId.Should().Be("sg-msg-123");

        captured.Should().NotBeNull();
        captured!.From.Email.Should().Be(FromAddress);
        captured.Personalizations[0].Tos[0].Email.Should().Be(Recipient);
        captured.Subject.Should().Be(Subject);
        captured.Contents.Should().Contain(c => c.Type == MimeType.Text && c.Value == Body);
        captured.Contents.Should().Contain(c => c.Type == MimeType.Html && c.Value == Body);
    }

    [Fact]
    public async Task SendAsync_On429_ReturnsRetryable()
    {
        var clientMock = new Mock<ISendGridClient>();
        var response = new Response((HttpStatusCode)429, null, null);

        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Error.Should().Contain("throttled");
    }

    [Fact]
    public async Task SendAsync_On5xx_ReturnsRetryable()
    {
        var clientMock = new Mock<ISendGridClient>();
        var response = new Response(HttpStatusCode.InternalServerError, null, null);

        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Error.Should().Contain("server error");
    }

    [Fact]
    public async Task SendAsync_On4xx_ReturnsNonRetryable()
    {
        var clientMock = new Mock<ISendGridClient>();
        var response = new Response(HttpStatusCode.BadRequest, null, null);

        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Error.Should().Contain("client error");
    }

    [Fact]
    public async Task SendAsync_OnException_ReturnsRetryable()
    {
        var clientMock = new Mock<ISendGridClient>();
        clientMock
            .Setup(c => c.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Exception("Network failure"));

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Subject, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeTrue();
        result.Error.Should().Contain("unexpected error");
    }
}
