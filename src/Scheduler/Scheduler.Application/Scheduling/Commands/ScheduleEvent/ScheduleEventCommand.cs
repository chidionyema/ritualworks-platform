using FluentValidation;
using Haworks.Scheduler.Application.Common.Interfaces;
using MediatR;

namespace Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;

public record ScheduleEventCommand(
    DateTimeOffset ScheduledTime,
    string TargetExchange,
    string RoutingKey,
    object Payload) : IRequest;

public class ScheduleEventCommandValidator : AbstractValidator<ScheduleEventCommand>
{
    public ScheduleEventCommandValidator()
    {
        RuleFor(v => v.ScheduledTime).Must(t => t > DateTimeOffset.UtcNow).WithMessage("ScheduledTime must be in the future");
        RuleFor(v => v.TargetExchange).NotEmpty();
        RuleFor(v => v.RoutingKey).NotEmpty();
        RuleFor(v => v.Payload).NotNull();
    }
}

public class ScheduleEventCommandHandler : IRequestHandler<ScheduleEventCommand>
{
    private readonly IEventScheduler _scheduler;

    public ScheduleEventCommandHandler(IEventScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public async Task Handle(ScheduleEventCommand request, CancellationToken cancellationToken)
    {
        await _scheduler.ScheduleEventAsync(
            request.ScheduledTime,
            request.TargetExchange,
            request.RoutingKey,
            request.Payload);
    }
}
