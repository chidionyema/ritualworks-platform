namespace Haworks.Analytics.Api.Application.Commands;

public record TrackEventCommand(
    string EventName,
    string UserId,
    string? SessionId,
    DateTime OccurredAt,
    IDictionary<string, object>? Metadata) : IRequest<Result<bool>>;
