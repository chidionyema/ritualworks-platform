namespace Haworks.Scheduler.Domain.Entities;

public class ScheduledEvent
{
    public Guid Id { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;
    public string TargetExchange { get; private set; } = default!;
    public string RoutingKey { get; private set; } = default!;
    public DateTimeOffset ScheduledTime { get; private set; }
    public string ScheduledBy { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public string HangfireJobId { get; private set; } = default!;

    /// <summary>PostgreSQL xmin concurrency token.</summary>
    public uint RowVersion { get; private set; }

    private ScheduledEvent() { }

    public static ScheduledEvent Create(
        string idempotencyKey,
        string targetExchange,
        string routingKey,
        DateTimeOffset scheduledTime,
        string scheduledBy,
        string hangfireJobId)
    {
        return new ScheduledEvent
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            TargetExchange = targetExchange,
            RoutingKey = routingKey,
            ScheduledTime = scheduledTime,
            ScheduledBy = scheduledBy,
            CreatedAt = DateTimeOffset.UtcNow,
            HangfireJobId = hangfireJobId
        };
    }
}
