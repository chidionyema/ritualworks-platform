using MediatR;
using Haworks.BuildingBlocks.Common;

namespace Haworks.Notifications.Application.Queries;

public sealed record GetNotificationQuery(Guid Id) : IRequest<Result<NotificationDto>>;

public sealed record NotificationDto(Guid Id, string Status, string? ErrorMessage);

internal sealed class GetNotificationQueryHandler : IRequestHandler<GetNotificationQuery, Result<NotificationDto>>
{
    public Task<Result<NotificationDto>> Handle(GetNotificationQuery request, CancellationToken ct)
        => throw new NotImplementedException("Track L1.G owns this body");
}
