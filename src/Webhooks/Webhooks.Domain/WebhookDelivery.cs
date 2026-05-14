using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Webhooks.Domain;

public enum DeliveryStatus
{
    Pending,
    Succeeded,
    Failed,
    Exhausted
}

public sealed class WebhookDelivery : AuditableEntity
{
    public Guid SubscriptionId { get; private set; }
    public string EventId { get; private set; } = null!;
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DeliveryStatus Status { get; private set; }
    public DateTime? NextAttemptAt { get; private set; }
    public int Attempts { get; private set; }
    public int? FinalStatus { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private readonly List<WebhookDeliveryAttempt> _attempts = [];
    public IReadOnlyCollection<WebhookDeliveryAttempt> DeliveryAttempts => _attempts.AsReadOnly();

    private WebhookDelivery() { } // EF

    public WebhookDelivery(Guid subscriptionId, string eventId, string eventType, string payload)
    {
        Id = Guid.NewGuid();
        SubscriptionId = subscriptionId;
        EventId = eventId;
        EventType = eventType;
        Payload = payload;
        Status = DeliveryStatus.Pending;
        CreatedAt = DateTime.UtcNow;
    }

    public void RecordAttempt(int httpStatus, string? responseBody, string? error, bool succeeded, DateTime? nextAttemptAt)
    {
        var attempt = new WebhookDeliveryAttempt(Id, Attempts, httpStatus, responseBody, error, succeeded);
        _attempts.Add(attempt);
        
        Attempts++;
        FinalStatus = httpStatus;
        
        if (succeeded)
        {
            Status = DeliveryStatus.Succeeded;
            CompletedAt = DateTime.UtcNow;
            NextAttemptAt = null;
        }
        else if (nextAttemptAt.HasValue)
        {
            Status = DeliveryStatus.Failed;
            NextAttemptAt = nextAttemptAt;
        }
        else
        {
            Status = DeliveryStatus.Exhausted;
            CompletedAt = DateTime.UtcNow;
            NextAttemptAt = null;
        }
    }
}
