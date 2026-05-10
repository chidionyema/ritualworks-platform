using System.Text.Json;

namespace Haworks.Audit.Api.Models;

public record AuditEventDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string EventType,
    string EntityType,
    string EntityId,
    string? ActorId,
    string? ActorType,
    string? CorrelationId,
    JsonElement Payload,
    JsonElement Metadata);

public record AuditPageResponse<T>(
    IEnumerable<T> Items,
    string? NextCursor);
