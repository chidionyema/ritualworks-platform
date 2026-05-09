using MediatR;
using Haworks.Notifications.Domain.Enums;
using Haworks.BuildingBlocks.Common;

namespace Haworks.Notifications.Application.Commands;

public sealed record SendNotificationCommand(
    string? UserId,
    string Recipient,
    NotificationChannel Channel,
    string TemplateId,
    NotificationPriority Priority,
    IDictionary<string, object> Variables,
    string? IdempotencyKey) : IRequest<Result<Guid>>;

internal sealed class SendNotificationCommandHandler : IRequestHandler<SendNotificationCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(SendNotificationCommand request, CancellationToken ct)
        => throw new NotImplementedException("Track L1.G owns this body");
}
