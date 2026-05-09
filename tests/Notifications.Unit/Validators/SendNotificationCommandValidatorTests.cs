using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Application.Validators;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Unit.Validators;

public sealed class SendNotificationCommandValidatorTests
{
    private readonly SendNotificationCommandValidator _validator = new();

    [Fact]
    public void Validate_WithUserIdAndTemplateId_PassesAllRules()
    {
        var command = NewCommand(userId: "user-1", recipient: string.Empty);

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithRecipientAndTemplateId_PassesAllRules()
    {
        var command = NewCommand(userId: null, recipient: "user@example.com");

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_MissingUserIdAndRecipient_ReportsRecipientError()
    {
        var command = NewCommand(userId: null, recipient: string.Empty);

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Recipient);
    }

    [Fact]
    public void Validate_MissingTemplateId_ReportsError()
    {
        var command = NewCommand(userId: "user-1", recipient: string.Empty, templateId: string.Empty);

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.TemplateId);
    }

    [Fact]
    public void Validate_NullVariables_ReportsError()
    {
        var command = new SendNotificationCommand(
            UserId: "user-1",
            Recipient: string.Empty,
            Channel: NotificationChannel.Email,
            TemplateId: "welcome",
            Priority: NotificationPriority.Normal,
            Variables: null!,
            IdempotencyKey: null);

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Variables);
    }

    [Fact]
    public void Validate_TemplateIdTooLong_ReportsError()
    {
        var command = NewCommand(userId: "user-1", recipient: string.Empty, templateId: new string('x', 101));

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.TemplateId);
    }

    [Fact]
    public void Validate_RecipientTooLong_ReportsError()
    {
        var command = NewCommand(userId: null, recipient: new string('a', 255) + "@example.com");

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Recipient);
    }

    [Fact]
    public void Validate_InvalidChannelEnum_ReportsError()
    {
        var command = new SendNotificationCommand(
            UserId: "user-1",
            Recipient: string.Empty,
            Channel: (NotificationChannel)999,
            TemplateId: "welcome",
            Priority: NotificationPriority.Normal,
            Variables: new Dictionary<string, object>(),
            IdempotencyKey: null);

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(c => c.Channel);
    }

    private static SendNotificationCommand NewCommand(
        string? userId = "user-1",
        string recipient = "user@example.com",
        string templateId = "welcome") =>
        new(
            UserId: userId,
            Recipient: recipient,
            Channel: NotificationChannel.Email,
            TemplateId: templateId,
            Priority: NotificationPriority.Normal,
            Variables: new Dictionary<string, object>(),
            IdempotencyKey: null);
}
