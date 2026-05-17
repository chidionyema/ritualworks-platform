using FluentValidation;
using Haworks.Webhooks.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Webhooks.Application.Subscriptions;

public sealed class CreateWebhookSubscriptionValidator : AbstractValidator<CreateWebhookSubscriptionCommand>
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "0.0.0.0"
    };

    public CreateWebhookSubscriptionValidator(IWebhooksDbContext db)
    {
        RuleFor(x => x.PartnerId).NotEmpty();
        RuleFor(x => x.Url).NotEmpty()
            .MaximumLength(2048).WithMessage("URL must not exceed 2048 characters.")
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
                return !BlockedHosts.Contains(uri.Host) && !IsPrivateIp(uri.Host);
            })
            .WithMessage("Private or internal URLs are not allowed.");
        RuleFor(x => x.Events).NotEmpty().WithMessage("At least one event must be selected.");

        RuleFor(x => x.PartnerId)
            .MustAsync(async (partnerId, ct) =>
            {
                var count = await db.Subscriptions
                    .CountAsync(s => s.PartnerId == partnerId && s.DeletedAt == null, ct);
                return count < 50;
            })
            .WithMessage("Maximum of 50 subscriptions per partner exceeded.");
    }

    internal static bool IsPrivateIp(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;

        // IPv6: block loopback (::1), link-local (fe80::/10), unique-local (fc00::/7)
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            var bytes6 = ip.GetAddressBytes();
            if ((bytes6[0] & 0xFE) == 0xFC) return true;  // fc00::/7 (unique-local)
            if ((bytes6[0] == 0xFE) && (bytes6[1] & 0xC0) == 0x80) return true; // fe80::/10 (link-local)
            return false;
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] == 10 ||                                         // 10.0.0.0/8
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||     // 172.16.0.0/12
            (bytes[0] == 192 && bytes[1] == 168) ||                      // 192.168.0.0/16
            bytes[0] == 127 ||                                           // 127.0.0.0/8
            (bytes[0] == 169 && bytes[1] == 254) ||                      // 169.254.0.0/16 (link-local)
            (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||   // 100.64.0.0/10 (CGNAT)
            bytes[0] == 0 ||                                             // 0.0.0.0/8
            (bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19) ||    // 198.18.0.0/15 (benchmark)
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||      // 192.0.0.0/24 (IETF protocol)
            (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||      // 192.0.2.0/24 (TEST-NET-1)
            (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||   // 198.51.100.0/24 (TEST-NET-2)
            (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||    // 203.0.113.0/24 (TEST-NET-3)
            bytes[0] >= 224;                                             // 224.0.0.0/4+ (multicast+reserved)
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
            .MaximumLength(2048).WithMessage("URL must not exceed 2048 characters.")
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
                return !BlockedHosts.Contains(uri.Host) && !CreateWebhookSubscriptionValidator.IsPrivateIp(uri.Host);
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
