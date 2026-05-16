using System.Text.Json;
using Haworks.Realtime.Api.Application.Common;
using StackExchange.Redis;

namespace Haworks.Realtime.Api.Infrastructure.Persistence;

/// <summary>
/// Redis-backed inbox using a LIST per user.
/// StoreMessageAsync uses LPUSH — O(1), atomic by design (single Redis command).
/// GetAndClearMessagesAsync uses a Lua script that atomically LRANGEs all items
/// then DELetes the key, preventing lost-update races between concurrent readers.
/// TTL is reset on every write to keep hot inboxes alive.
/// </summary>
public class RedisInboxService : IInboxService
{
    private const int InboxTtlDays = 7;
    private const int MaxInboxSize = 200;

    // Atomically fetch all items and delete the list.
    private static readonly LuaScript GetAndClearScript = LuaScript.Prepare("""
        local items = redis.call('LRANGE', @key, 0, -1)
        redis.call('DEL', @key)
        return items
        """);

    private readonly IConnectionMultiplexer _redis;

    public RedisInboxService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task StoreMessageAsync(Guid userId, object message, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = InboxKey(userId);
        var json = JsonSerializer.Serialize(message);

        // LPUSH is atomic; cap the list to avoid unbounded growth.
        await db.ListLeftPushAsync(key, json);
        await db.ListTrimAsync(key, 0, MaxInboxSize - 1);
        await db.KeyExpireAsync(key, TimeSpan.FromDays(InboxTtlDays));
    }

    public async Task<IEnumerable<object>> GetAndClearMessagesAsync(Guid userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = InboxKey(userId);

        var result = (RedisValue[]?) await db.ScriptEvaluateAsync(
            GetAndClearScript,
            new { key = (RedisKey) key });

        if (result is null || result.Length == 0)
            return Enumerable.Empty<object>();

        // Items were pushed left; reverse so oldest is first.
        return result
            .Reverse()
            .Select(v => JsonSerializer.Deserialize<object>((string) v!)!)
            .ToList();
    }

    private static string InboxKey(Guid userId) => $"inbox:{userId}";
}
