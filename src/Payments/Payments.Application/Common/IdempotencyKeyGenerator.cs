using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haworks.Payments.Application.Interfaces;

namespace Haworks.Payments.Application.Common;

/// <summary>
/// Generates deterministic, collision-resistant idempotency keys.
/// Uses SHA256 hashing to create consistent keys from input components.
/// </summary>
public sealed class IdempotencyKeyGenerator : IIdempotencyKeyGenerator
{
    /// <inheritdoc />
    public string GenerateKey(string userId, string operation, params string[] components)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        // Purely deterministic from inputs — no time component.
        // The caller passes OrderId as a component, so different orders produce different keys.
        // Same OrderId always produces same key → Stripe deduplicates retries correctly.
        var payload = new
        {
            UserId = userId,
            Operation = operation,
            Components = components.OrderBy(c => c, StringComparer.Ordinal).ToList(),
        };

        var json = JsonSerializer.Serialize(payload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(hash);
    }
}
