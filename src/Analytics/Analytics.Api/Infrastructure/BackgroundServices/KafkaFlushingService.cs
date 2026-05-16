using System.Text.Json;
using Confluent.Kafka;
using Haworks.Analytics.Api.Infrastructure.Buffer;
using Haworks.Analytics.Api.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.Analytics.Api.Infrastructure.BackgroundServices;

public sealed class KafkaFlushingService : BackgroundService
{
    private const string TopicName = "analytics.clickstream";
    private const int BatchSize = 100;
    private const int MaxRetries = 3;

    private readonly IEventBuffer _buffer;
    private readonly IProducer<string, string>? _producer;
    private readonly ILogger<KafkaFlushingService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public KafkaFlushingService(
        IEventBuffer buffer,
        ILogger<KafkaFlushingService> logger,
        IProducer<string, string>? producer = null)
    {
        _buffer = buffer;
        _producer = producer;
        _logger = logger;

        // Wire the buffer into the static metric gauge now that DI is resolved.
        AnalyticsMetrics.Configure(buffer);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("KafkaFlushingService starting. Topic={Topic} Producer={Mode}",
            TopicName, _producer is null ? "mock" : "real");

        if (_producer is null)
        {
            _logger.LogWarning(
                "No Kafka producer registered (ConnectionStrings:kafka not set). " +
                "Events will be consumed from the buffer but NOT produced to Kafka.");
        }

        try
        {
            var batch = new List<ClickstreamEvent>(BatchSize);

            // Wait for the first item in a batch (blocks until available or cancelled).
            await foreach (var first in _buffer.DequeueAllAsync(stoppingToken))
            {
                try
                {
                    batch.Add(first);

                    // Drain up to BatchSize-1 additional items already in the channel.
                    _buffer.TryReadBatch(batch, BatchSize - 1);

                    await ProduceBatchAsync(batch, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw; // propagate shutdown signal
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error processing batch of {Count} events. Dropping batch and continuing.",
                        batch.Count);
                    AnalyticsMetrics.EventsDropped.Add(batch.Count);
                }
                finally
                {
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("KafkaFlushingService stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in KafkaFlushingService outer loop.");
        }
        finally
        {
            _producer?.Flush(TimeSpan.FromSeconds(10));
        }
    }

    private async Task ProduceBatchAsync(IReadOnlyList<ClickstreamEvent> batch, CancellationToken ct)
    {
        foreach (var @event in batch)
        {
            var payload = JsonSerializer.Serialize(@event, JsonOpts);
            var message = new Message<string, string>
            {
                Key = @event.UserId.ToString(),
                Value = payload
            };

            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (_producer is not null)
                    {
                        await _producer.ProduceAsync(TopicName, message, ct);
                    }
                    else
                    {
                        _logger.LogDebug("Mock produce: {EventName} at {OccurredAt}",
                            @event.EventName, @event.OccurredAt);
                    }

                    AnalyticsMetrics.EventsFlushed.Add(1);
                    break;
                }
                catch (ProduceException<string, string> ex) when (attempt < MaxRetries)
                {
                    _logger.LogWarning(ex,
                        "Kafka produce attempt {Attempt}/{Max} failed for event {EventName}. Retrying.",
                        attempt, MaxRetries, @event.EventName);

                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
                }
                catch (ProduceException<string, string> ex)
                {
                    _logger.LogError(ex,
                        "Kafka produce failed after {Max} attempts for event {EventName}. Dropping.",
                        MaxRetries, @event.EventName);
                    AnalyticsMetrics.EventsDropped.Add(1);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    public override void Dispose()
    {
        _producer?.Dispose();
        base.Dispose();
    }
}
