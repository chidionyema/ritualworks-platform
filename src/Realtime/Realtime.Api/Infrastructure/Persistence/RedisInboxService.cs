using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Haworks.Realtime.Api.Application.Common;

namespace Haworks.Realtime.Api.Infrastructure.Persistence;

public class RedisInboxService : IInboxService
{
    private readonly IDistributedCache _cache;

    public RedisInboxService(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task StoreMessageAsync(Guid userId, object message, CancellationToken ct = default)
    {
        var key = $"inbox:{userId}";
        var existingJson = await _cache.GetStringAsync(key, ct);
        var messages = string.IsNullOrEmpty(existingJson) 
            ? new List<object>() 
            : JsonSerializer.Deserialize<List<object>>(existingJson) ?? new List<object>();
        
        messages.Add(message);
        await _cache.SetStringAsync(key, JsonSerializer.Serialize(messages), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7)
        }, ct);
    }

    public async Task<IEnumerable<object>> GetAndClearMessagesAsync(Guid userId, CancellationToken ct = default)
    {
        var key = $"inbox:{userId}";
        var existingJson = await _cache.GetStringAsync(key, ct);
        if (string.IsNullOrEmpty(existingJson)) return Enumerable.Empty<object>();

        await _cache.RemoveAsync(key, ct);
        return JsonSerializer.Deserialize<List<object>>(existingJson) ?? Enumerable.Empty<object>();
    }
}
