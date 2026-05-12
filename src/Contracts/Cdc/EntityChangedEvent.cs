using System.Text.Json.Nodes;

namespace Haworks.Contracts.Cdc;

/// <summary>
/// Platform-wide standardized CDC event. Represents a single row change
/// captured from a Postgres WAL stream.
/// </summary>
public sealed record EntityChangedEvent
{
    public required Guid EventId { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string ChangeType { get; init; } // created, updated, deleted
    public required DateTimeOffset OccurredAt { get; init; }
    public required string SourceService { get; init; }
    public required string SourceTransactionId { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public JsonNode? PayloadBefore { get; init; }
    public JsonNode? PayloadAfter { get; init; }
    public CdcMetadata Metadata { get; init; } = new();
}

public sealed record CdcMetadata
{
    public string? CorrelationId { get; init; }
    public string? ActorId { get; init; }
    public string? ActorType { get; init; } // user, system
    public string? Trigger { get; init; } // api, background, migration
}
