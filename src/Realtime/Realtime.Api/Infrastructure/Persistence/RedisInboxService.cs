using System.Text.Json;
using Haworks.Realtime.Api.Application.Common;
using StackExchange.Redis;

namespace Haworks.Realtime.Api.Infrastructure.Persistence;

/// <summary>
/// Redis-backed inbox using a LIST per user.
///
/// Write path: LPUSH + LTRIM (caps at <see cref="InboxConstants.MaxInboxSize"/>).
/// Read path: LRANGE (non-destructive peek).
/// Ack path: DEL (only after SignalR confirms delivery).
///
/// Each message carries a <see cref="InboxMessage.MessageId"/> (Guid) for client-side deduplication.
/// </summary>
public class RedisInboxService : IInboxService
{
    private const int InboxTtlDays = 7;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisInboxService> _logger;

    public RedisInboxService(IConnectionMultiplexer redis, ILogger<RedisInboxService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task StoreMessageAsync(Guid userId, object message, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = InboxKey(userId);
        var messageId = Guid.NewGuid();

        var envelope = new InboxEnvelope(messageId, message);
        var json = JsonSerializer.Serialize(envelope);

        // LPUSH is atomic; cap the list to avoid unbounded growth.
        await db.ListLeftPushAsync(key, json);
        await db.ListTrimAsync(key, 0, InboxConstants.MaxInboxSize - 1);
        await db.KeyExpireAsync(key, TimeSpan.FromDays(InboxTtlDays));

        _logger.LogInformation(
            "Inbox message stored. UserId={UserId}, MessageId={MessageId}, MessageType={MessageType}",
            userId, messageId, message.GetType().Name);
    }

    public async Task<IReadOnlyList<InboxMessage>> GetMessagesAsync(Guid userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = InboxKey(userId);

        var items = await db.ListRangeAsync(key, 0, -1);
        if (items.Length == 0)
            return Array.Empty<InboxMessage>();

        // Items were pushed left; reverse so oldest is first.
        var messages = new List<InboxMessage>(items.Length);
        for (var i = items.Length - 1; i >= 0; i--)
        {
            var envelope = JsonSerializer.Deserialize<InboxEnvelope>((string)items[i]!);
            if (envelope is not null)
            {
                messages.Add(new InboxMessage(
                    envelope.MessageId,
                    envelope.Data.GetType().Name,
                    envelope.Data));
            }
        }

        return messages;
    }

    public async Task AcknowledgeMessagesAsync(Guid userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = InboxKey(userId);
        await db.KeyDeleteAsync(key);

        _logger.LogInformation(
            "Inbox acknowledged and cleared. UserId={UserId}",
            userId);
    }

    private static string InboxKey(Guid userId) => $"inbox:{userId}";

    private sealed record InboxEnvelope(Guid MessageId, object Data);
}
