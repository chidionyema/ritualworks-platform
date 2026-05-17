namespace Haworks.Realtime.Api.Application.Common;

/// <summary>
/// Represents a single inbox message with a unique identifier for client-side deduplication.
/// </summary>
public sealed record InboxMessage(Guid MessageId, string MessageType, object Data);

public interface IInboxService
{
    /// <summary>
    /// Stores a message in the user's inbox with server-side deduplication.
    /// If <paramref name="messageId"/> was already stored, this is a no-op (idempotent).
    /// </summary>
    Task StoreMessageAsync(Guid userId, Guid messageId, string messageType, object message, CancellationToken ct = default);

    /// <summary>
    /// Reads all pending messages without removing them (peek).
    /// </summary>
    Task<IReadOnlyList<InboxMessage>> GetMessagesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Acknowledges delivery — trims only the count of messages actually delivered,
    /// preserving any that arrived during the flush window.
    /// </summary>
    Task AcknowledgeMessagesAsync(Guid userId, int deliveredCount, CancellationToken ct = default);
}

public static class InboxConstants
{
    public const int MaxInboxSize = 1000;
}
