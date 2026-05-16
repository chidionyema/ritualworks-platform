namespace Haworks.Scheduler.Application.Common.Interfaces;

public interface IEventScheduler
{
    Task ScheduleEventAsync(DateTimeOffset scheduledTime, string targetExchange, string routingKey, string payload);
}
