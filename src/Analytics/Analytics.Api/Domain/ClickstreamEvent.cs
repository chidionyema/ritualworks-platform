namespace Haworks.Analytics.Api.Domain;

public record ClickstreamEvent(
    string EventName,
    Guid UserId,
    string? SessionId,
    DateTime OccurredAt,
    IDictionary<string, object>? Metadata);
