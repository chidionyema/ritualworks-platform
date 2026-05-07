using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Infrastructure.Webhooks;

/// <summary>
/// Guards against duplicate webhook processing.
/// Uses a combination of distributed cache (fast check) and database (source of truth).
/// </summary>
public sealed class WebhookIdempotencyGuard : IWebhookIdempotencyGuard
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IDistributedCache _cache;
    private readonly ILogger<WebhookIdempotencyGuard> _logger;

    private const string CacheKeyPrefix = "webhook-idemp:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    public WebhookIdempotencyGuard(
        IPaymentRepository paymentRepository,
        IDistributedCache cache,
        ILogger<WebhookIdempotencyGuard> logger)
    {
        _paymentRepository = paymentRepository ?? throw new ArgumentNullException(nameof(paymentRepository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> IsAlreadyProcessedAsync(
        PaymentProvider provider,
        string eventId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return false;
        }

        var cacheKey = $"{CacheKeyPrefix}{provider}:{eventId}";

        // 1. Fast path: check distributed cache
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached != null)
        {
            _logger.LogDebug("Webhook {EventId} found in idempotency cache", eventId);
            return true;
        }

        // 2. Slow path: check database
        var exists = await _paymentRepository.WebhookEventExistsAsync(provider, eventId, ct);
        if (exists)
        {
            // Backfill cache
            await _cache.SetStringAsync(cacheKey, "1", new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration
            }, ct);
        }

        return exists;
    }

    /// <inheritdoc />
    public async Task MarkProcessedAsync(
        PaymentProvider provider,
        string eventId,
        string eventType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return;
        }

        var cacheKey = $"{CacheKeyPrefix}{provider}:{eventId}";

        // We don't insert into DB here because it's usually done in the same transaction
        // as the state mutation in the consumer. This method is used to mark it in cache
        // AFTER successful processing.
        
        await _cache.SetStringAsync(cacheKey, "1", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration
        }, ct);

        _logger.LogInformation(
            "Marked webhook event {EventId} from {Provider} as processed in cache",
            eventId, provider);
    }
}
