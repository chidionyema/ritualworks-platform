namespace Haworks.Analytics.Api.Domain;

public record ClickstreamEvent(
    Guid EventId,
    string EventName,
    Guid UserId,
    string? SessionId,
    DateTime OccurredAt,
    DateTime IngestedAt,
    long SequenceNumber,
    IDictionary<string, object>? Metadata);
