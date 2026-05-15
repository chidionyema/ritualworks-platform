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
        // Unbounded for high throughput, or bounded with drop strategy
        _channel = Channel.CreateUnbounded<ClickstreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(ClickstreamEvent @event, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(@event, ct);
    }

    public IAsyncEnumerable<ClickstreamEvent> DequeueAllAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
