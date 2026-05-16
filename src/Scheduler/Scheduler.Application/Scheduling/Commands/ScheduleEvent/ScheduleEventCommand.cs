using System.Text.Json;
using FluentValidation;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.Scheduler.Application.Common.Interfaces;
using MediatR;

namespace Haworks.Scheduler.Application.Scheduling.Commands.ScheduleEvent;

public record ScheduleEventCommand(
    string IdempotencyKey,
    DateTimeOffset ScheduledTime,
    string TargetExchange,
    string RoutingKey,
    string Payload) : IRequest<ScheduleEventResult>;

public record ScheduleEventResult(string JobId, bool AlreadyExisted);

public class ScheduleEventCommandValidator : AbstractValidator<ScheduleEventCommand>
{
    public ScheduleEventCommandValidator()
    {
        RuleFor(v => v.IdempotencyKey)
            .NotEmpty()
            .MaximumLength(200);

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

public class ScheduleEventCommandHandler : IRequestHandler<ScheduleEventCommand, ScheduleEventResult>
{
    private readonly IEventScheduler _scheduler;
    private readonly ICurrentUserService _currentUser;

    public ScheduleEventCommandHandler(IEventScheduler scheduler, ICurrentUserService currentUser)
    {
        _scheduler = scheduler;
        _currentUser = currentUser;
    }

    public async Task<ScheduleEventResult> Handle(ScheduleEventCommand request, CancellationToken cancellationToken)
    {
        var scheduledBy = _currentUser.UserId ?? "system";

        var jobId = await _scheduler.ScheduleEventAsync(
            request.IdempotencyKey,
            request.ScheduledTime,
            request.TargetExchange,
            request.RoutingKey,
            request.Payload,
            scheduledBy);

        return new ScheduleEventResult(jobId, AlreadyExisted: false);
    }
}
