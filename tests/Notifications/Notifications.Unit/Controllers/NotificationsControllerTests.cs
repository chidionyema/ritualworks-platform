using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Haworks.Notifications.Api.Controllers;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Queries;
using Haworks.Notifications.Domain.Enums;
using Haworks.BuildingBlocks.Common;

namespace Haworks.Notifications.Unit.Controllers;

[Trait("Category", "Unit")]
public sealed class NotificationsControllerTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly NotificationsController _sut;

    public NotificationsControllerTests()
    {
        _sut = new NotificationsController(_mediator.Object);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    [Fact]
    public async Task Send_OnSuccess_Returns201Created()
    {
        // Arrange
        var notificationId = Guid.NewGuid();
        var command = NewCommand();
        _mediator
            .Setup(m => m.Send(command, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(notificationId));

        // Act
        var actionResult = await _sut.Send(command, CancellationToken.None);

        // Assert
        var created = actionResult.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(NotificationsController.Get));
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["id"].Should().Be(notificationId);
        created.Value.Should().Be(notificationId);
    }

    [Fact]
    public async Task Send_OnValidationFailure_ReturnsObjectResultWith400()
    {
        // Arrange
        var command = NewCommand();
        var failure = Result.Failure<Guid>(Error.Validation("Send.Invalid", "bad input"));
        _mediator
            .Setup(m => m.Send(command, It.IsAny<CancellationToken>()))
            .ReturnsAsync(failure);

        // Act
        var actionResult = await _sut.Send(command, CancellationToken.None);

        // Assert — Result.ToCreatedActionResult delegates to ToErrorActionResult on failure.
        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Get_WhenNotFound_Returns404()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.Is<GetNotificationQuery>(q => q.Id == id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<NotificationDto>(new Error("Notifications.NotFound", "not found", ErrorType.NotFound)));

        // Act
        var actionResult = await _sut.Get(id, CancellationToken.None);

        // Assert
        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Get_WhenFound_ReturnsOkWithDto()
    {
        // Arrange
        var id = Guid.NewGuid();
        var dto = new NotificationDto(
            Id: id,
            Status: nameof(NotificationStatus.Created),
            ErrorMessage: null,
            Recipient: "user@example.com",
            Channel: nameof(NotificationChannel.Email),
            TemplateId: "welcome",
            Priority: nameof(NotificationPriority.Normal),
            UserId: "user-1",
            SentAt: null,
            DeliveredAt: null,
            IdempotencyKey: "k",
            Attempts: Array.Empty<NotificationAttemptDto>());
        _mediator
            .Setup(m => m.Send(It.Is<GetNotificationQuery>(q => q.Id == id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        // Act
        var actionResult = await _sut.Get(id, CancellationToken.None);

        // Assert
        var ok = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    private static SendNotificationCommand NewCommand() =>
        new(
            UserId: "user-1",
            Recipient: "user@example.com",
            Channel: NotificationChannel.Email,
            TemplateId: "welcome",
            Priority: NotificationPriority.Normal,
            Variables: new Dictionary<string, object>(),
            IdempotencyKey: null);
}
