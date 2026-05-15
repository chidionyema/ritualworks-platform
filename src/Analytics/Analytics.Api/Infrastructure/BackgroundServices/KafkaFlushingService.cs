using Haworks.Analytics.Api.Infrastructure.Buffer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Analytics.Api.Infrastructure.BackgroundServices;

public class KafkaFlushingService : BackgroundService
{
    private readonly IEventBuffer _buffer;
    private readonly ILogger<KafkaFlushingService> _logger;

    public KafkaFlushingService(IEventBuffer buffer, ILogger<KafkaFlushingService> logger)
    {
        _buffer = buffer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Kafka Flushing Service is starting.");

        try
        {
            await foreach (var @event in _buffer.DequeueAllAsync(stoppingToken))
            {
                // Mock Kafka Sink: In reality, this would batch and send to Kafka
                _logger.LogDebug("Flushing event to Mock Kafka: {EventName} at {OccurredAt}", @event.EventName, @event.OccurredAt);
                
                // Simulate some I/O if needed, or just let it spin
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Kafka Flushing Service is stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while flushing events to Kafka.");
        }
    }
}
