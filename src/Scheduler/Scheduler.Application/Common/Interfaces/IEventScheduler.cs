namespace Haworks.Scheduler.Application.Common.Interfaces;

public interface IEventScheduler
{
    Task<string> ScheduleEventAsync(
        string idempotencyKey,
        DateTimeOffset scheduledTime,
        string targetExchange,
        string routingKey,
        string payload,
        string? scheduledBy);
}
