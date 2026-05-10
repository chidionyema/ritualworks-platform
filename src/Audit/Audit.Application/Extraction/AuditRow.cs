using System.Text.Json;

namespace Haworks.Audit.Application.Extraction;

/// <summary>
/// Output of <see cref="IAuditExtractor{T}.Extract"/>: the materialised
/// row ready for redaction + insert.
///
/// Per docs/agent-briefs/audit-service-spec.md § 5.1 — payload + metadata
/// flow through unchanged here; the redactor (L1.A) strips secrets later.
/// </summary>
public sealed record AuditRow(
    DateTimeOffset OccurredAt,
    string EventType,
    string EntityType,
    string EntityId,
    string? ActorId,
    string? ActorType,
    string? CorrelationId,
    JsonElement Payload,
    JsonElement Metadata);
