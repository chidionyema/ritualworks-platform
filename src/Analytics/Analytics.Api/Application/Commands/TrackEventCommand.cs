using System.Text.Json.Serialization;
using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Analytics.Api.Application.Commands;

public record TrackEventCommand(
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] string EventName,
    [property: JsonRequired] Guid UserId,
    string? SessionId,
    [property: JsonRequired] DateTime OccurredAt,
    IDictionary<string, object>? Metadata) : IRequest<Result<bool>>;
