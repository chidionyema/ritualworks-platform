namespace Haworks.Audit.Application.Export;

/// <summary>
/// Filter for <see cref="IAuditExportJob.EnqueueAsync"/>. Same shape as
/// the query API's filter minus cursor / limit (exports always stream
/// the full filtered range).
/// </summary>
public sealed record AuditExportRequest(
    string? EntityId,
    string? EntityType,
    string? EventType,
    DateTimeOffset From,
    DateTimeOffset To);
