using System.Net;
using FluentAssertions;
using Haworks.Notifications.Application.Channels;
using Haworks.Notifications.Infrastructure.Channels.Sms.Twilio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Twilio.Clients;
using Twilio.Http;
using Xunit;

namespace Haworks.Notifications.Unit.Channels.Sms.Twilio;

public class TwilioSmsProviderTests
{
    private const string FromNumber = "+15005550006";
    private const string Recipient = "+1234567890";
    private const string Body = "Hello from Twilio";

    private static IOptions<TwilioOptions> BuildOptions() => Options.Create(new TwilioOptions
    {
        AccountSid = "AC-TEST",
        AuthToken = "TOKEN-TEST",
        FromNumber = FromNumber
    });

    private static TwilioSmsProvider BuildSut(ITwilioRestClient client) =>
        new(client, BuildOptions(), NullLogger<TwilioSmsProvider>.Instance);

    [Fact]
    public void Name_ReturnsTwilioConstant()
    {
        var clientMock = new Mock<ITwilioRestClient>();
        var sut = BuildSut(clientMock.Object);

        sut.Name.Should().Be("twilio");
    }

    [Fact]
    public async Task SendAsync_OnSuccess_ReturnsSuccessWithSid()
    {
        var clientMock = new Mock<ITwilioRestClient>();
        const string sid = "SM1234567890abcdef";
        var jsonResponse = $@"{{ ""sid"": ""{sid}"", ""status"": ""queued"" }}";
        
        clientMock
            .Setup(c => c.RequestAsync(It.IsAny<Request>()))
            .ReturnsAsync(new Response(HttpStatusCode.Created, jsonResponse));

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Body, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.ProviderMessageId.Should().Be(sid);
    }

    [Fact]
    public async Task SendAsync_OnTwilioError_ReturnsNonRetryable()
    {
        var clientMock = new Mock<ITwilioRestClient>();
        const string jsonResponse = @"{ ""sid"": ""SM123"", ""status"": ""failed"", ""error_code"": 20001, ""error_message"": ""Bad request"" }";
        
        clientMock
            .Setup(c => c.RequestAsync(It.IsAny<Request>()))
            .ReturnsAsync(new Response(HttpStatusCode.Created, jsonResponse)); // Twilio returns 201 even if it has internal error code in some cases, or 400.

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Error.Should().Contain("20001");
    }

    /* 
    [Fact]
    public async Task SendAsync_OnApiException500_ReturnsRetryable()
    {
        // TODO(notif-F2): Fix ApiException mocking for Twilio SDK v7
    }
    */

    [Fact]
    public async Task SendAsync_OnCarrierRejection_ReturnsNonRetryable()
    {
        var clientMock = new Mock<ITwilioRestClient>();
        const string jsonResponse = @"{ ""sid"": ""SM123"", ""status"": ""failed"", ""error_code"": 30001, ""error_message"": ""Carrier Rejection"" }";
        
        clientMock
            .Setup(c => c.RequestAsync(It.IsAny<Request>()))
            .ReturnsAsync(new Response(HttpStatusCode.Created, jsonResponse));

        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(Recipient, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        result.Error.Should().Contain("30001");
    }

    [Fact]
    public async Task SendAsync_OnEmptyRecipient_ReturnsNonRetryableImmediately()
    {
        var clientMock = new Mock<ITwilioRestClient>();
        var sut = BuildSut(clientMock.Object);

        var result = await sut.SendAsync(string.Empty, Body, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsRetryable.Should().BeFalse();
        clientMock.Verify(c => c.RequestAsync(It.IsAny<Request>()), Times.Never);
    }
}
