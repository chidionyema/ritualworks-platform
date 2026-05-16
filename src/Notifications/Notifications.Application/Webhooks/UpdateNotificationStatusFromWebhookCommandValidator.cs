using FluentValidation;

namespace Haworks.Notifications.Application.Webhooks;

internal sealed class UpdateNotificationStatusFromWebhookCommandValidator
    : AbstractValidator<UpdateNotificationStatusFromWebhookCommand>
{
    public UpdateNotificationStatusFromWebhookCommandValidator()
    {
        RuleFor(x => x.Provider).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ProviderMessageId).NotEmpty().MaximumLength(500);
        RuleFor(x => x.EventType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.RawPayload).NotEmpty();
    }
}
