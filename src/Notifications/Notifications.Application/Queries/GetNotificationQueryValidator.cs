using FluentValidation;

namespace Haworks.Notifications.Application.Queries;

public class GetNotificationQueryValidator : AbstractValidator<GetNotificationQuery>
{
    public GetNotificationQueryValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}
