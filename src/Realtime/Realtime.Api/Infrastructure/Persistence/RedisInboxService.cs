using System.Text.Json;
using Haworks.Realtime.Api.Application.Common;
using StackExchange.Redis;

namespace Haworks.Realtime.Api.Infrastructure.Persistence;

public sealed class RedisInboxService : IInboxService
{
    private const int InboxTtlDays = 7;
    private const int DedupTtlDays = 3;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisInboxService> _logger;

    public RedisInboxService(IConnectionMultiplexer redis, ILogger<RedisInboxService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task StoreMessageAsync(Guid userId, Guid messageId, string messageType, object message, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var inboxKey = InboxKey(userId);
        var dedupKey = DedupKey(userId);

        // C2 Fix: Atomic dedup check — if messageId already seen, skip (idempotent)
        var alreadySeen = await db.SetAddAsync(dedupKey, messageId.ToString());
        if (!alreadySeen)
        {
            _logger.LogDebug(
                "Duplicate message skipped. UserId={UserId}, MessageId={MessageId}",
                userId, messageId);
            return;
        }

        // C1 Fix: Store messageType explicitly in envelope (not derived from GetType after deserialization)
        var envelope = new InboxEnvelope(messageId, messageType, JsonSerializer.Serialize(message));
        var json = JsonSerializer.Serialize(envelope);

        // Atomic: LPUSH + LTRIM + EXPIRE via transaction
        var tran = db.CreateTransaction();
        _ = tran.ListLeftPushAsync(inboxKey, json);
        _ = tran.ListTrimAsync(inboxKey, 0, InboxConstants.MaxInboxSize - 1);
        _ = tran.KeyExpireAsync(inboxKey, TimeSpan.FromDays(InboxTtlDays));
        _ = tran.KeyExpireAsync(dedupKey, TimeSpan.FromDays(DedupTtlDays));
        await tran.ExecuteAsync();

        _logger.LogInformation(
            "Inbox message stored. UserId={UserId}, MessageId={MessageId}, MessageType={MessageType}",
            userId, messageId, messageType);
    }

    public async Task<IReadOnlyList<InboxMessage>> GetMessagesAsync(Guid userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = InboxKey(userId);

        var items = await db.ListRangeAsync(key, 0, -1);
        if (items.Length == 0)
            return Array.Empty<InboxMessage>();

        var messages = new List<InboxMessage>(items.Length);
        for (var i = items.Length - 1; i >= 0; i--)
        {
            var envelope = JsonSerializer.Deserialize<InboxEnvelope>((string)items[i]!);
            if (envelope is not null)
            {
                // C1 Fix: Use stored MessageType and raw JSON data
                var data = JsonSerializer.Deserialize<JsonElement>(envelope.DataJson);
                messages.Add(new InboxMessage(envelope.MessageId, envelope.MessageType, data));
            }
        }

        return messages;
    }

    // H2 Fix: Trim only the count actually delivered (not DEL), preserving new arrivals
    public async Task AcknowledgeMessagesAsync(Guid userId, int deliveredCount, CancellationToken ct = default)
    {
        if (deliveredCount <= 0) return;

        var db = _redis.GetDatabase();
        var key = InboxKey(userId);

        // LTRIM keeps elements [0, newLength-1]. We want to remove the LAST deliveredCount elements
        // (oldest, pushed right). Trim from 0 to (currentLength - deliveredCount - 1).
        var currentLength = await db.ListLengthAsync(key);
        if (currentLength <= deliveredCount)
        {
            await db.KeyDeleteAsync(key);
        }
        else
        {
            await db.ListTrimAsync(key, 0, currentLength - deliveredCount - 1);
        }

        _logger.LogInformation(
            "Inbox acknowledged. UserId={UserId}, DeliveredCount={Count}",
            userId, deliveredCount);
    }

    private static string InboxKey(Guid userId) => $"realtime:inbox:{userId}";
    private static string DedupKey(Guid userId) => $"realtime:inbox-dedup:{userId}";

    private sealed record InboxEnvelope(Guid MessageId, string MessageType, string DataJson);
}
