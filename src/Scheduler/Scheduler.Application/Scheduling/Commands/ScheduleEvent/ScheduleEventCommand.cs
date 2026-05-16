using System.Text.Json;
using FluentValidation;
using Haworks.Scheduler.Application.Common.Interfaces;
using MediatR;

namespace Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;

public record ScheduleEventCommand(
    DateTimeOffset ScheduledTime,
    string TargetExchange,
    string RoutingKey,
    string Payload) : IRequest;

public class ScheduleEventCommandValidator : AbstractValidator<ScheduleEventCommand>
{
    private static readonly DateTimeOffset MaxScheduledTime =
        DateTimeOffset.UtcNow.AddYears(1);

    public ScheduleEventCommandValidator()
    {
        RuleFor(v => v.ScheduledTime)
            .Must(t => t > DateTimeOffset.UtcNow)
            .WithMessage("ScheduledTime must be in the future")
            .Must(t => t <= DateTimeOffset.UtcNow.AddYears(1))
            .WithMessage("ScheduledTime must be within 1 year from now");

        RuleFor(v => v.TargetExchange).NotEmpty();
        RuleFor(v => v.RoutingKey).NotEmpty();

        RuleFor(v => v.Payload)
            .NotNull()
            .MaximumLength(65536)
            .WithMessage("Payload must not exceed 65536 characters")
            .Must(BeValidJson)
            .WithMessage("Payload must be valid JSON");
    }

    private static bool BeValidJson(string? payload)
    {
        if (payload is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
