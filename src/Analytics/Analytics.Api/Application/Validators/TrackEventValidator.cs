using Haworks.Analytics.Api.Application.Commands;

namespace Haworks.Analytics.Api.Application.Validators;

public class TrackEventValidator : AbstractValidator<TrackEventCommand>
{
    public TrackEventValidator()
    {
        RuleFor(x => x.EventName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.OccurredAt).LessThanOrEqualTo(x => DateTime.UtcNow.AddMinutes(5))
            .WithMessage("OccurredAt cannot be too far in the future.");
    }
}
