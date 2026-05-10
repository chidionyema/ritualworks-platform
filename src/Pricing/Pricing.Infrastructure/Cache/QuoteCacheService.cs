using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;

namespace Haworks.Pricing.Infrastructure.Cache;

public interface IQuoteCacheService
{
    Task SetQuoteAsync<T>(string key, T quote, CancellationToken cancellationToken = default);
    Task<T?> GetQuoteAsync<T>(string key, CancellationToken cancellationToken = default);
}

public class QuoteCacheService(IDistributedCache cache) : IQuoteCacheService
{
    public async Task SetQuoteAsync<T>(string key, T quote, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(quote);
        await cache.SetStringAsync(key, json, cancellationToken);
    }

    public async Task<T?> GetQuoteAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var json = await cache.GetStringAsync(key, cancellationToken);
        return string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json);
    }
}
