using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Haworks.Payments.Api.Webhooks;

/// <summary>
/// Validates a Stripe webhook signature header per Stripe's documented
/// scheme: the <c>Stripe-Signature</c> header is a comma-separated list of
/// <c>k=v</c> pairs. Required entries:
///   <c>t</c>     — unix timestamp the webhook was sent at
///   <c>v1=…</c>  — HMAC-SHA256 of <c>"{t}.{rawPayload}"</c> using the
///                  webhook secret as the HMAC key, hex-lower-encoded
///
/// Implementation lifted from Stripe's reference behaviour so we don't need
/// the Stripe.net SDK in payments-svc just for verification.
/// </summary>
public static class StripeSignatureValidator
{
    /// <summary>5-minute tolerance: the gap between Stripe's signing
    /// timestamp and our clock. Catches replay attempts on stale events.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static bool TryValidate(
        string rawPayload,
        string stripeSignatureHeader,
        string webhookSecret,
        TimeSpan? tolerance = null,
        DateTimeOffset? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeSignatureHeader);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookSecret);
        ArgumentNullException.ThrowIfNull(rawPayload);

        var parsed = Parse(stripeSignatureHeader);
        if (!parsed.TryGetValue("t", out var tValue) ||
            !long.TryParse(tValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTimestamp))
        {
            return false;
        }

        if (!parsed.TryGetValue("v1", out var providedSignature))
        {
            return false;
        }

        var now = utcNow ?? DateTimeOffset.UtcNow;
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        if (Math.Abs((now - signedAt).TotalSeconds) > (tolerance ?? DefaultTolerance).TotalSeconds)
        {
            return false;
        }

        var signedPayload = $"{unixTimestamp}.{rawPayload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var computedHex = Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex),
            Encoding.UTF8.GetBytes(providedSignature.ToLowerInvariant()));
    }

    private static Dictionary<string, string> Parse(string header)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0 || idx == part.Length - 1) continue;

            var key = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            // Stripe sends multiple v1 lines for key rotation. Last writer wins
            // for the dict; ParsingOnly cares about presence, not multiplicity.
            dict[key] = value;
        }
        return dict;
    }
}
