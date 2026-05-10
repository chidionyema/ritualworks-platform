using FluentAssertions;
using Haworks.Notifications.Api.Controllers;
using Haworks.Notifications.Application.Webhooks;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text;
using System.IO;
using Xunit;

namespace Haworks.Notifications.Unit.Webhooks;

public class WebhooksControllerTests
{
    private readonly Mock<IPublishEndpoint> _publishMock = new();
    private readonly Mock<IOptions<WebhookOptions>> _optionsMock = new();
    private readonly WebhooksController _sut;

    public WebhooksControllerTests()
    {
        _optionsMock.Setup(o => o.Value).Returns(new WebhookOptions());
        _sut = new WebhooksController(
            _publishMock.Object,
            _optionsMock.Object,
            NullLogger<WebhooksController>.Instance);
        
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    [Fact]
    public async Task SendGrid_ValidPayload_PublishesEvents()
    {
        var payload = "[{\"event\": \"delivered\", \"sg_message_id\": \"msg123.abc\"}]";
        var bytes = Encoding.UTF8.GetBytes(payload);
        _sut.Request.Body = new MemoryStream(bytes);
        _sut.Request.Headers["X-Twilio-Email-Event-Webhook-Signature"] = "sig";
        _sut.Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"] = "ts";

        var result = await _sut.SendGrid(CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }
}
