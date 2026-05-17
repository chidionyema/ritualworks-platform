using System.Text.Json;
using Haworks.Scheduler.Application.Common.Interfaces;
using Haworks.Scheduler.Infrastructure.Messaging;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Haworks.Scheduler.Infrastructure.Persistence;

public class HangfireEventScheduler : IEventScheduler
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<HangfireEventScheduler> _logger;

    public HangfireEventScheduler(IBackgroundJobClient backgroundJobClient, ILogger<HangfireEventScheduler> logger)
    {
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
    }

    public Task<string> ScheduleEventAsync(
        string idempotencyKey,
        DateTimeOffset scheduledTime,
        string targetExchange,
        string routingKey,
        string payload,
        string? scheduledBy)
    {
        var auditPayload = EnrichPayloadWithAudit(payload, scheduledBy, idempotencyKey);

        var jobId = _backgroundJobClient.Schedule<EventPublisherJob>(
            job => job.PublishAsync(targetExchange, routingKey, auditPayload),
            scheduledTime);

        _logger.LogInformation(
            "Scheduled event with IdempotencyKey={IdempotencyKey} JobId={JobId} ScheduledBy={ScheduledBy}",
            idempotencyKey, jobId, scheduledBy);

        return Task.FromResult(jobId);
    }

    private static string EnrichPayloadWithAudit(string payload, string? scheduledBy, string idempotencyKey)
    {
        using var doc = JsonDocument.Parse(payload);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteString("__scheduledBy", scheduledBy ?? "system");
            writer.WriteString("__idempotencyKey", idempotencyKey);
            writer.WriteString("__scheduledAt", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
