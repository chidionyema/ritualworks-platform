using FluentValidation;

namespace Haworks.Realtime.Api.Application.Notifications;

public class SendNotificationCommandValidator : AbstractValidator<SendNotificationCommand>
{
    public SendNotificationCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.MessageType).NotEmpty();
        RuleFor(x => x.Data).NotNull();
    }
}
