using FluentValidation;
using Haworks.Webhooks.Application.Common;

namespace Haworks.Webhooks.Application.Subscriptions;

public sealed class CreateWebhookSubscriptionValidator : AbstractValidator<CreateWebhookSubscriptionCommand>
{
    public CreateWebhookSubscriptionValidator()
    {
        RuleFor(x => x.PartnerId).NotEmpty();
        RuleFor(x => x.Url).NotEmpty()
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .WithMessage("A valid absolute URL is required.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return string.Equals(uri.Scheme, "https", StringComparison.Ordinal);
            })
            .WithMessage("Only HTTPS URLs are allowed.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return !WebhookSsrfGuard.BlockedHosts.Contains(uri.Host) && !WebhookSsrfGuard.IsPrivateIp(uri.Host);
            })
            .WithMessage("Private or internal URLs are not allowed.");
        RuleFor(x => x.Events).NotEmpty().WithMessage("At least one event must be selected.");
    }
}

public sealed class UpdateWebhookSubscriptionValidator : AbstractValidator<UpdateWebhookSubscriptionCommand>
{
    public UpdateWebhookSubscriptionValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Url).NotEmpty()
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .WithMessage("A valid absolute URL is required.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return string.Equals(uri.Scheme, "https", StringComparison.Ordinal);
            })
            .WithMessage("Only HTTPS URLs are allowed.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return !WebhookSsrfGuard.BlockedHosts.Contains(uri.Host) && !WebhookSsrfGuard.IsPrivateIp(uri.Host);
            })
            .WithMessage("Private or internal URLs are not allowed.");
        RuleFor(x => x.Events).NotEmpty().WithMessage("At least one event must be selected.");
    }
}

public sealed class DeleteWebhookSubscriptionCommandValidator : AbstractValidator<DeleteWebhookSubscriptionCommand>
{
    public DeleteWebhookSubscriptionCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}

public sealed class GetWebhookSubscriptionQueryValidator : AbstractValidator<GetWebhookSubscriptionQuery>
{
    public GetWebhookSubscriptionQueryValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
