using FluentValidation;

namespace Haworks.Webhooks.Application.Subscriptions;

internal sealed class RotateWebhookSubscriptionSecretCommandValidator : AbstractValidator<RotateWebhookSubscriptionSecretCommand>
{
    public RotateWebhookSubscriptionSecretCommandValidator()
    {
        RuleFor(x => x.Id).NotEqual(Guid.Empty);
    }
}
