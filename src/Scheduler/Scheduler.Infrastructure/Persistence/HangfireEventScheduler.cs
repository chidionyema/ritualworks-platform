using Haworks.Scheduler.Application.Common.Interfaces;
using Haworks.Scheduler.Infrastructure.Messaging;
using Hangfire;

namespace Haworks.Scheduler.Infrastructure.Persistence;

public class HangfireEventScheduler : IEventScheduler
{
    private readonly IBackgroundJobClient _backgroundJobClient;

    public HangfireEventScheduler(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient;
    }

    public Task ScheduleEventAsync(DateTimeOffset scheduledTime, string targetExchange, string routingKey, object payload)
    {
        _backgroundJobClient.Schedule<EventPublisherJob>(
            job => job.PublishAsync(targetExchange, routingKey, payload),
            scheduledTime);
        
        return Task.CompletedTask;
    }
}
