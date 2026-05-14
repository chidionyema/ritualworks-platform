using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Webhooks.Domain;

public sealed class WebhookDeliveryAttempt : AuditableEntity
{
    public Guid DeliveryId { get; private set; }
    public int AttemptIndex { get; private set; }
    public DateTime StartedAt { get; private set; } = DateTime.UtcNow;
    public int? DurationMs { get; private set; }
    public int? HttpStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public string? Error { get; private set; }
    public bool Succeeded { get; private set; }

    private WebhookDeliveryAttempt() { } // EF

    public WebhookDeliveryAttempt(Guid deliveryId, int attemptIndex, int? httpStatus, string? responseBody, string? error, bool succeeded)
    {
        Id = Guid.NewGuid();
        DeliveryId = deliveryId;
        AttemptIndex = attemptIndex;
        HttpStatus = httpStatus;
        ResponseBody = responseBody;
        Error = error;
        Succeeded = succeeded;
    }

    public void SetDuration(int durationMs)
    {
        DurationMs = durationMs;
    }
}
