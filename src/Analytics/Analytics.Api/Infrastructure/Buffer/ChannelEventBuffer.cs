using System.Threading.Channels;

namespace Haworks.Analytics.Api.Infrastructure.Buffer;

public interface IEventBuffer
{
    ValueTask EnqueueAsync(ClickstreamEvent @event, CancellationToken ct = default);
    IAsyncEnumerable<ClickstreamEvent> DequeueAllAsync(CancellationToken ct = default);
}

public class ChannelEventBuffer : IEventBuffer
{
    private readonly Channel<ClickstreamEvent> _channel;

    public ChannelEventBuffer()
    {
        // Staff-level hardening: Bounded channel prevents memory exhaustion (OOM).
        // DropOldest strategy ensures the most recent (and relevant) events are preserved 
        // if the background consumer slows down.
        _channel = Channel.CreateBounded<ClickstreamEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(ClickstreamEvent @event, CancellationToken ct = default)
    {
        // Staff-level hardening: Metrics instrumentation
        Haworks.Analytics.Api.Infrastructure.Telemetry.AnalyticsMetrics.EventsEnqueued.Add(1);

        // TryWrite is used here because with DropOldest, WriteAsync would only block
        // if the channel was full without a drop strategy. 
        if (!_channel.Writer.TryWrite(@event))
        {
            Haworks.Analytics.Api.Infrastructure.Telemetry.AnalyticsMetrics.EventsDropped.Add(1);
        }
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ClickstreamEvent> DequeueAllAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
