using System.Text.Json;

namespace Haworks.Audit.Domain;

/// <summary>
/// One row in the append-only <c>audit_events</c> table.
///
/// Mirrors the schema in docs/agent-briefs/audit-service-spec.md § 4.
/// EF Core maps via <c>AuditDbContext</c>; the partitioning + indexes
/// live in the L1.B migration (raw SQL — partitioned tables aren't
/// expressible via the EF fluent API).
///
/// L0 ships only the entity shape. L1.A populates rows via
/// <c>IAuditExtractor</c>; L1.B writes them via <c>IAuditWriter</c>.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? ActorId { get; private set; }
    public string? ActorType { get; private set; }
    public string? CorrelationId { get; private set; }
    public JsonDocument Payload { get; private set; } = JsonDocument.Parse("{}");
    public JsonDocument Metadata { get; private set; } = JsonDocument.Parse("{}");

    private AuditEvent() { }

    public static AuditEvent Create(
        DateTimeOffset occurredAt,
        string eventType,
        string entityType,
        string entityId,
        string? actorId,
        string? actorType,
        string? correlationId,
        JsonDocument payload,
        JsonDocument metadata) => new()
        {
            Id = Guid.NewGuid(),
            OccurredAt = occurredAt,
            ReceivedAt = DateTimeOffset.UtcNow,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            ActorId = actorId,
            ActorType = actorType,
            CorrelationId = correlationId,
            Payload = payload,
            Metadata = metadata,
        };
}
