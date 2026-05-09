using Haworks.BuildingBlocks.Persistence;
using Haworks.Notifications.Domain.Enums;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Domain.Entities;

/// <summary>
/// notifications-svc Notification aggregate. Owns its own state-machine
/// transitions; cross-context references are opaque ids only (UserId is
/// the identity-svc string FK; TemplateId is a notifications-svc handle).
///
/// State machine (per docs/architecture/notification-service.md §4):
///   Created -> Rendering -> Queued -> Sent -> Delivered
///                                          \-> Bounced (terminal, suppression-worthy)
///                                          \-> Complained (terminal, suppression-worthy)
///                                          \-> Failed (terminal, all providers exhausted)
///   Created -> Failed (validation/render failure pre-queue)
///
/// All state transitions are guarded; invalid transitions throw
/// <see cref="InvalidOperationException"/>. Terminal states cannot be
/// re-entered (no double-dispatch, no double-delivery audit drift).
/// </summary>
public sealed class Notification : AuditableEntity
{
    private readonly List<DeliveryAttempt> _deliveryAttempts = new();

    public string? UserId { get; private set; }
    public string Recipient { get; private set; } = string.Empty;
    public NotificationChannel Channel { get; private set; }
    public string TemplateId { get; private set; } = string.Empty;
    public NotificationStatus Status { get; private set; }
    public NotificationPriority Priority { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public string? IdempotencyKey { get; private set; }

    /// <summary>Provider's correlation id once the notification has been Sent (null beforehand).</summary>
    public string? ProviderMessageId { get; private set; }

    public IReadOnlyCollection<DeliveryAttempt> DeliveryAttempts => _deliveryAttempts.AsReadOnly();

    private Notification() { }

    /// <summary>Factory for a new Notification in <see cref="NotificationStatus.Created"/>.</summary>
    /// <param name="recipient">Channel-appropriate recipient (email address, E.164 phone, push token).</param>
    /// <param name="channel">Delivery channel.</param>
    /// <param name="templateId">Template identifier (resolved later by Application).</param>
    /// <param name="idempotencyKey">SHA-256 hash from the caller; required for dedupe.</param>
    /// <param name="userId">Optional opaque identity-svc user id (null for anonymous flows).</param>
    /// <param name="priority">Delivery priority; defaults to Normal.</param>
    /// <param name="subject">Initial subject line (may be overwritten by render).</param>
    /// <param name="body">Initial body (may be overwritten by render).</param>
    public static Notification Create(
        string recipient,
        NotificationChannel channel,
        string templateId,
        string idempotencyKey,
        string? userId = null,
        NotificationPriority priority = NotificationPriority.Normal,
        string subject = "",
        string body = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        return new Notification
        {
            Recipient = recipient,
            Channel = channel,
            TemplateId = templateId,
            IdempotencyKey = idempotencyKey,
            UserId = userId,
            Priority = priority,
            Subject = subject ?? string.Empty,
            Body = body ?? string.Empty,
            Status = NotificationStatus.Created,
        };
    }

    /// <summary>Created -> Rendering. Invoked when the renderer pulls the notification.</summary>
    public void MarkRendering()
    {
        EnsureTransitionFrom(nameof(MarkRendering), NotificationStatus.Created);
        Status = NotificationStatus.Rendering;
        Touch();
    }

    /// <summary>Rendering -> Queued. Invoked once the rendered payload is on the channel queue.</summary>
    public void MarkQueued()
    {
        EnsureTransitionFrom(nameof(MarkQueued), NotificationStatus.Rendering);
        Status = NotificationStatus.Queued;
        Touch();
    }

    /// <summary>
    /// Queued -> Sent. Invoked when the provider returns a 2xx accepting the message.
    /// Use <see cref="MarkSent(string)"/> to also persist the provider's message id.
    /// </summary>
    public void MarkSent()
    {
        EnsureTransitionFrom(nameof(MarkSent), NotificationStatus.Queued);
        Status = NotificationStatus.Sent;
        SentAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>Queued -> Sent and capture the provider's message id.</summary>
    public void MarkSent(string providerMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerMessageId);
        EnsureTransitionFrom(nameof(MarkSent), NotificationStatus.Queued);
        Status = NotificationStatus.Sent;
        ProviderMessageId = providerMessageId;
        SentAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>Sent -> Delivered. Invoked from the provider's delivery webhook.</summary>
    public void MarkDelivered()
    {
        EnsureTransitionFrom(nameof(MarkDelivered), NotificationStatus.Sent);
        Status = NotificationStatus.Delivered;
        DeliveredAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>Sent -> Bounced. Hard bounce reported by the provider; recipient is suppression-worthy.</summary>
    public void MarkBounced(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureTransitionFrom(nameof(MarkBounced), NotificationStatus.Sent);
        Status = NotificationStatus.Bounced;
        ErrorMessage = reason;
        Touch();
    }

    /// <summary>Delivered -> Complained. Recipient marked the message as spam.</summary>
    public void MarkComplained()
    {
        EnsureTransitionFrom(nameof(MarkComplained), NotificationStatus.Delivered);
        Status = NotificationStatus.Complained;
        Touch();
    }

    /// <summary>Any non-terminal state -> Failed. All providers exhausted or unrecoverable error.</summary>
    public void MarkFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureNotTerminal(nameof(MarkFailed));
        Status = NotificationStatus.Failed;
        ErrorMessage = reason;
        Touch();
    }

    /// <summary>
    /// Append a provider attempt (success or failure). Allowed in any non-terminal state — retries
    /// and provider failovers are normal during Queued/Sent.
    /// </summary>
    public void RecordAttempt(DeliveryAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        EnsureNotTerminal(nameof(RecordAttempt));
        _deliveryAttempts.Add(attempt);
        Touch();
    }

    /// <summary>
    /// Convenience overload: builds a <see cref="DeliveryAttempt"/> from primitive provider results
    /// (2xx <paramref name="statusCode"/> => success). Use when the caller has only the wire-level result.
    /// </summary>
    public void RecordAttempt(string provider, int? statusCode, string? errorMessage, string? providerMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var isSuccess = statusCode is >= 200 and < 300;
        var attempt = new DeliveryAttempt(
            AttemptedAt: DateTime.UtcNow,
            ProviderName: provider,
            ProviderMessageId: providerMessageId,
            IsSuccess: isSuccess,
            ErrorMessage: errorMessage);
        RecordAttempt(attempt);
    }

    private void EnsureTransitionFrom(string operation, NotificationStatus required)
    {
        if (Status != required)
        {
            throw new InvalidOperationException(
                $"Notification {Id}: cannot {operation} from status {Status}; required {required}.");
        }
    }

    private void EnsureNotTerminal(string operation)
    {
        if (IsTerminal(Status))
        {
            throw new InvalidOperationException(
                $"Notification {Id}: cannot {operation} from terminal status {Status}.");
        }
    }

    private static bool IsTerminal(NotificationStatus s) =>
        s is NotificationStatus.Delivered
          or NotificationStatus.Bounced
          or NotificationStatus.Complained
          or NotificationStatus.Failed
          or NotificationStatus.Suppressed;

    private void Touch() => LastModifiedDate = DateTime.UtcNow;
}
