using System.Threading.Channels;

namespace Haworks.Analytics.Api.Infrastructure.Buffer;

public interface IEventBuffer
{
    ValueTask EnqueueAsync(ClickstreamEvent @event, CancellationToken ct = default);
    IAsyncEnumerable<ClickstreamEvent> DequeueAllAsync(CancellationToken ct = default);
    int TryReadBatch(IList<ClickstreamEvent> destination, int maxItems);
    int Count { get; }
}

public class ChannelEventBuffer : IEventBuffer
{
    private const int Capacity = 10_000;
    private readonly Channel<ClickstreamEvent> _channel;
    private int _count;

    public ChannelEventBuffer()
    {
        _channel = Channel.CreateBounded<ClickstreamEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Approximate number of events currently in the buffer.</summary>
    public int Count => _count;

    /// <summary>
    /// Attempts to synchronously read up to <paramref name="maxItems"/> events into
    /// <paramref name="destination"/>, returning the number added.  Used by the flushing
    /// service to drain the channel in batches without blocking.
    /// </summary>
    public int TryReadBatch(IList<ClickstreamEvent> destination, int maxItems)
    {
        var read = 0;
        while (read < maxItems && _channel.Reader.TryRead(out var item))
        {
            Interlocked.Decrement(ref _count);
            destination.Add(item);
            read++;
        }
        return read;
    }

    public ValueTask EnqueueAsync(ClickstreamEvent @event, CancellationToken ct = default)
    {
        Haworks.Analytics.Api.Infrastructure.Telemetry.AnalyticsMetrics.EventsEnqueued.Add(1);

        if (_channel.Writer.TryWrite(@event))
        {
            Interlocked.Increment(ref _count);
        }
        else
        {
            // DropOldest silently drops; count stays the same but we record the drop.
            Haworks.Analytics.Api.Infrastructure.Telemetry.AnalyticsMetrics.EventsDropped.Add(1);
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ClickstreamEvent> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _count);
            yield return item;
        }
    }
}
