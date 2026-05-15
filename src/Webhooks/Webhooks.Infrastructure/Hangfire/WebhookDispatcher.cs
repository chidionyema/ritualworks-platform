using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Haworks.Webhooks.Application.Interfaces;
using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Webhooks.Infrastructure.Hangfire;

public sealed class WebhookDispatcher(
    IWebhooksDbContext db,
    IHttpClientFactory httpFactory,
    ILogger<WebhookDispatcher> logger) : IWebhookDispatcher
{
    public async Task DispatchAsync(Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.Deliveries
            .Include(d => d.DeliveryAttempts)
            .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);

        if (delivery == null || delivery.Status == DeliveryStatus.Succeeded || delivery.Status == DeliveryStatus.Exhausted)
        {
            return;
        }

        var sub = await db.Subscriptions.FindAsync([delivery.SubscriptionId], ct);
        if (sub == null || !sub.IsActive || sub.DeletedAt != null)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = SignPayload(delivery.Payload, timestamp, sub.Secret);

        var request = new HttpRequestMessage(HttpMethod.Post, sub.Url);
        request.Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json");
        request.Headers.Add("Webhook-Id", delivery.Id.ToString());
        request.Headers.Add("Webhook-Timestamp", timestamp.ToString());
        request.Headers.Add("Webhook-Signature", $"t={timestamp},v1={signature}");
        request.Headers.UserAgent.ParseAdd("ritualworks-webhooks/1.0");

        var client = httpFactory.CreateClient("WebhookDispatcher");
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int? httpStatus = null;
        string? responseBody = null;
        string? error = null;
        bool succeeded = false;

        try
        {
            using var response = await client.SendAsync(request, ct);
            httpStatus = (int)response.StatusCode;
            succeeded = response.IsSuccessStatusCode;
            responseBody = await response.Content.ReadAsStringAsync(ct);
            if (responseBody.Length > 8192) responseBody = responseBody[..8192] + "... [TRUNCATED]";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(ex, "Failed to deliver webhook {DeliveryId} to {Url}", delivery.Id, sub.Url);
        }
        finally
        {
            stopwatch.Stop();
        }

        DateTime? nextAttemptAt = succeeded ? null : CalculateNextAttempt(delivery.Attempts + 1);

        delivery.RecordAttempt(httpStatus ?? 0, responseBody, error, succeeded, nextAttemptAt);
        await db.SaveChangesAsync(ct);

        if (!succeeded && nextAttemptAt.HasValue)
        {
            var delay = nextAttemptAt.Value - DateTime.UtcNow;
            BackgroundJob.Schedule<IWebhookDispatcher>(x => x.DispatchAsync(delivery.Id, CancellationToken.None), delay);
        }
    }

    private string SignPayload(string payload, long timestamp, string secret)
    {
        var signedPayload = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static DateTime? CalculateNextAttempt(int attempts)
    {
        int[] intervals = [1, 2, 4, 8, 16, 32, 60, 120, 240, 480, 960, 1440, 1440, 1440, 1440, 1440];
        if (attempts >= intervals.Length) return null;
        
        return DateTime.UtcNow.AddMinutes(intervals[attempts]);
    }
}
