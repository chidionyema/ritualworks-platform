namespace Haworks.Realtime.Api.Application.Common;

/// <summary>
/// Represents a single inbox message with a unique identifier for client-side deduplication.
/// </summary>
public sealed record InboxMessage(Guid MessageId, string MessageType, object Data);

public interface IInboxService
{
    /// <summary>
    /// Stores a message in the user's inbox. Caps at <see cref="InboxConstants.MaxInboxSize"/> messages.
    /// </summary>
    Task StoreMessageAsync(Guid userId, object message, CancellationToken ct = default);

    /// <summary>
    /// Reads all pending messages without removing them (peek).
    /// </summary>
    Task<IReadOnlyList<InboxMessage>> GetMessagesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Acknowledges delivery — removes messages from the inbox after SignalR confirms send.
    /// </summary>
    Task AcknowledgeMessagesAsync(Guid userId, CancellationToken ct = default);
}

public static class InboxConstants
{
    public const int MaxInboxSize = 1000;
}
