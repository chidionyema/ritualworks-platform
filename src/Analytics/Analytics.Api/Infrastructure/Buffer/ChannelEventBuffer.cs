using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Haworks.Analytics.Api.Infrastructure.Buffer;

public interface IEventBuffer
{
    ValueTask EnqueueAsync(ClickstreamEvent @event, CancellationToken ct = default);
    IAsyncEnumerable<ClickstreamEvent> DequeueAllAsync(CancellationToken ct = default);
    int TryReadBatch(IList<ClickstreamEvent> destination, int maxItems);
    int Count { get; }

    /// <summary>Check if an EventId has already been enqueued (deduplication).</summary>
    bool ContainsEventId(Guid eventId);

    /// <summary>Get the next monotonically-increasing sequence number for ordering.</summary>
    long NextSequenceNumber();
}

public class ChannelEventBuffer : IEventBuffer
{
    private const int Capacity = 10_000;
    private const int DeduplicationWindowSize = 50_000;
    private readonly Channel<ClickstreamEvent> _channel;
    private int _count;
    private long _sequenceNumber;

    private readonly ConcurrentDictionary<Guid, byte> _seenEventIds = new();
    private readonly ConcurrentQueue<Guid> _evictionQueue = new();

    public ChannelEventBuffer()
    {
        _channel = Channel.CreateBounded<ClickstreamEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int Count => _count;

    public bool ContainsEventId(Guid eventId)
    {
        return _seenEventIds.ContainsKey(eventId);
    }

    public long NextSequenceNumber()
    {
        return Interlocked.Increment(ref _sequenceNumber);
    }

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

        TrackEventId(@event.EventId);

        if (_channel.Writer.TryWrite(@event))
        {
            Interlocked.Increment(ref _count);
        }
        else
        {
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

    private void TrackEventId(Guid eventId)
    {
        if (_seenEventIds.TryAdd(eventId, 0))
        {
            _evictionQueue.Enqueue(eventId);

            while (_seenEventIds.Count > DeduplicationWindowSize && _evictionQueue.TryDequeue(out var oldest))
            {
                _seenEventIds.TryRemove(oldest, out _);
            }
        }
    }
}
