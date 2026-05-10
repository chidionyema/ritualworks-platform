using Haworks.Audit.Domain;

namespace Haworks.Audit.Application.Queries;

public interface IAuditQueryService
{
    Task<AuditPageResult> ListAsync(AuditQueryRequest request, CancellationToken ct);
    Task<AuditEvent?> GetByIdAsync(Guid id, DateTimeOffset occurredAt, CancellationToken ct);
}

public record AuditQueryRequest(
    string? EntityType,
    string? EntityId,
    string? EventType,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Cursor,
    int Limit);

public record AuditPageResult(
    IEnumerable<AuditEvent> Items,
    string? NextCursor);
