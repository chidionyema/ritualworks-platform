using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Haworks.Notifications.Application.Common.Idempotency;

/// <summary>
/// Generates deterministic, collision-resistant idempotency keys for notification sends.
/// Mirrors the Payments idempotency pattern: SHA-256 over a canonical input.
/// </summary>
/// <remarks>
/// Key shape: SHA-256(hex) over the canonical string
/// "tenantId|templateId|recipient(canonicalized)|callerKey".
///
/// Recipient is canonicalized to lowercase invariant + trim so that the same
/// human recipient always hashes to the same key regardless of casing.
/// When <c>callerSuppliedKey</c> is null/blank, the canonical string omits the
/// caller component (so the key is purely derived from the other components).
/// </remarks>
public sealed class IdempotencyKeyGenerator : IIdempotencyKeyGenerator
{
    private const char Separator = '|';

    /// <inheritdoc />
    public string Generate(string? userId, string templateId, string recipient, string? callerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

        var tenant = string.IsNullOrWhiteSpace(userId) ? string.Empty : userId.Trim();
        var canonicalRecipient = recipient.Trim().ToLowerInvariant();
        var caller = string.IsNullOrWhiteSpace(callerKey) ? string.Empty : callerKey.Trim();

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{tenant}{Separator}{templateId.Trim()}{Separator}{canonicalRecipient}{Separator}{caller}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
