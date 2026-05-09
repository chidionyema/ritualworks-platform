using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Haworks.Notifications.Application.Suppression;

/// <summary>
/// L1.D suppression-list service. Hashes recipients with SHA-256 over a
/// canonicalized form (lowercase email or E.164 phone) before any persistence
/// or lookup so we never store raw PII in the suppression table.
/// </summary>
public sealed class SuppressionService : ISuppressionService
{
    private readonly ISuppressionRepository _repository;
    private readonly ILogger<SuppressionService> _logger;

    public SuppressionService(
        ISuppressionRepository repository,
        ILogger<SuppressionService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> IsSuppressedAsync(string recipient, NotificationChannel channel, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

        var hash = HashRecipient(recipient, channel);
        return await _repository.ExistsAsync(hash, channel).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(
        string recipient,
        NotificationChannel channel,
        string reason,
        string? sourceEventId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var hash = HashRecipient(recipient, channel);

        var suppression = SuppressionFactory.Create(hash, channel, reason, sourceEventId);

        await _repository.AddAsync(suppression).ConfigureAwait(false);

        _logger.LogInformation(
            "Recipient suppressed. Channel: {Channel}, Reason: {Reason}, RecipientHash: {RecipientHash}",
            channel, reason, hash);
    }

    /// <summary>
    /// Public helper so other layers (e.g. webhook bounce handlers) can compute
    /// the same hash without coupling to the service interface.
    /// </summary>
    public static string HashRecipient(string recipient, NotificationChannel channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

        var canonical = Canonicalize(recipient, channel);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Canonicalize(string recipient, NotificationChannel channel)
    {
        var trimmed = recipient.Trim();

        return channel switch
        {
            // Email: lowercase whole address (RFC 5321 local-part is technically
            // case-sensitive, but every real-world MTA folds — and our use case
            // is suppression matching, not strict equality).
            NotificationChannel.Email => trimmed.ToLowerInvariant(),

            // SMS: strip whitespace, dashes, parens — keep leading + and digits.
            // Callers are expected to already pass E.164; this just normalizes
            // common formatting so "+1 (415) 555-2671" matches "+14155552671".
            NotificationChannel.Sms => NormalizePhone(trimmed),

            // Push: device token / endpoint — exact (case-sensitive) match.
            NotificationChannel.Push => trimmed,

            _ => trimmed.ToLowerInvariant(),
        };
    }

    private static string NormalizePhone(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == '+' || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
