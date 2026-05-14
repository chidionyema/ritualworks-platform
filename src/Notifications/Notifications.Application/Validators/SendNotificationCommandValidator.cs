using FluentValidation;
using Haworks.Notifications.Application.Commands;
using Haworks.Notifications.Domain.Enums;

namespace Haworks.Notifications.Application.Validators;

/// <summary>
/// Validates a <see cref="SendNotificationCommand"/> before it reaches the
/// handler. Auto-discovered by FluentValidation's assembly scan registered in
/// <c>DependencyInjection.AddNotificationsApplication</c>.
/// </summary>
internal sealed class SendNotificationCommandValidator : AbstractValidator<SendNotificationCommand>
{
    private const int MaxRecipientLength = 254;
    private const int MaxTemplateIdLength = 100;
    private const int MaxIdempotencyKeyLength = 200;

    public SendNotificationCommandValidator()
    {
        RuleFor(x => x.TemplateId)
            .NotEmpty().WithMessage("TemplateId is required.")
            .MaximumLength(MaxTemplateIdLength);

        // A notification needs *some* recipient: either a UserId (resolved
        // server-side from the user table) or an explicit Recipient fallback
        // (email/phone/push token). At least one MUST be present.
        RuleFor(x => x)
            .Must(HaveRecipient)
            .WithMessage("Either UserId or Recipient must be provided.")
            .OverridePropertyName(nameof(SendNotificationCommand.Recipient));

        RuleFor(x => x.Recipient)
            .MaximumLength(MaxRecipientLength)
            .When(x => !string.IsNullOrWhiteSpace(x.Recipient));

        RuleFor(x => x.Recipient)
            .EmailAddress()
            .When(x => x.Channel == NotificationChannel.Email && !string.IsNullOrWhiteSpace(x.Recipient));

        RuleFor(x => x.Recipient)
            .Matches(@"^\+[1-9]\d{6,14}$")
            .When(x => x.Channel == NotificationChannel.Sms && !string.IsNullOrWhiteSpace(x.Recipient));

        RuleFor(x => x.Variables)
            .NotNull().WithMessage("Variables must not be null (use an empty dictionary if no substitutions are required).");

        RuleFor(x => x.IdempotencyKey)
            .MaximumLength(MaxIdempotencyKeyLength)
            .When(x => !string.IsNullOrWhiteSpace(x.IdempotencyKey));

        RuleFor(x => x.Channel)
            .IsInEnum();

        RuleFor(x => x.Priority)
            .IsInEnum();
    }

    private static bool HaveRecipient(SendNotificationCommand command) =>
        !string.IsNullOrWhiteSpace(command.UserId) ||
        !string.IsNullOrWhiteSpace(command.Recipient);
}
