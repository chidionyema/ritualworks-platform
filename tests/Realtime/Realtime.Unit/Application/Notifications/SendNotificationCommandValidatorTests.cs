using FluentValidation.TestHelper;
using Haworks.Realtime.Api.Application.Notifications;
using Xunit;

namespace Haworks.Realtime.Unit.Application.Notifications;

public class SendNotificationCommandValidatorTests
{
    private readonly SendNotificationCommandValidator _validator;

    public SendNotificationCommandValidatorTests()
    {
        _validator = new SendNotificationCommandValidator();
    }

    [Fact]
    public void Should_Have_Error_When_UserId_Is_Empty()
    {
        var command = new SendNotificationCommand { UserId = Guid.Empty };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Should_Have_Error_When_MessageType_Is_Empty()
    {
        var command = new SendNotificationCommand { MessageType = string.Empty };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.MessageType);
    }

    [Fact]
    public void Should_Not_Have_Error_When_Command_Is_Valid()
    {
        var command = new SendNotificationCommand 
        { 
            UserId = Guid.NewGuid(), 
            MessageType = "Test", 
            Data = new { foo = "bar" } 
        };
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
