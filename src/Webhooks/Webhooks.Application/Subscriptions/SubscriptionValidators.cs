using FluentValidation;

namespace Haworks.Webhooks.Application.Subscriptions;

public sealed class CreateWebhookSubscriptionValidator : AbstractValidator<CreateWebhookSubscriptionCommand>
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "0.0.0.0"
    };

    public CreateWebhookSubscriptionValidator()
    {
        RuleFor(x => x.PartnerId).NotEmpty();
        RuleFor(x => x.Url).NotEmpty()
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .WithMessage("A valid absolute URL is required.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return uri.Scheme == "https";
            })
            .WithMessage("Only HTTPS URLs are allowed.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return !BlockedHosts.Contains(uri.Host) && !IsPrivateIp(uri.Host);
            })
            .WithMessage("Private or internal URLs are not allowed.");
        RuleFor(x => x.Events).NotEmpty().WithMessage("At least one event must be selected.");
    }

    internal static bool IsPrivateIp(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (
            bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            bytes[0] == 127);
    }
}

public sealed class UpdateWebhookSubscriptionValidator : AbstractValidator<UpdateWebhookSubscriptionCommand>
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "0.0.0.0"
    };

    public UpdateWebhookSubscriptionValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Url).NotEmpty()
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _))
            .WithMessage("A valid absolute URL is required.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return uri.Scheme == "https";
            })
            .WithMessage("Only HTTPS URLs are allowed.")
            .Must(url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
                return !BlockedHosts.Contains(uri.Host) && !CreateWebhookSubscriptionValidator.IsPrivateIp(uri.Host);
            })
            .WithMessage("Private or internal URLs are not allowed.");
        RuleFor(x => x.Events).NotEmpty().WithMessage("At least one event must be selected.");
    }
}
