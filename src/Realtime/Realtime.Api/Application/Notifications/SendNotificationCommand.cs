using Haworks.BuildingBlocks.Common;
using MediatR;

namespace Haworks.Realtime.Api.Application.Notifications;

public record SendNotificationCommand : IRequest<Result<Unit>>
{
    public Guid UserId { get; init; }
    public string MessageType { get; init; } = string.Empty;
    public object Data { get; init; } = new { };
}
