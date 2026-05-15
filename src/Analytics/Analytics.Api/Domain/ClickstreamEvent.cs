namespace Haworks.Analytics.Api.Domain;

public record ClickstreamEvent(
    string EventName,
    string UserId,
    string? SessionId,
    DateTime OccurredAt,
    IDictionary<string, object>? Metadata);
