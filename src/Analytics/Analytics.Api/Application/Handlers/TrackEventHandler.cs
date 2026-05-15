using Haworks.Analytics.Api.Application.Commands;
using Haworks.Analytics.Api.Infrastructure.Buffer;

namespace Haworks.Analytics.Api.Application.Handlers;

public class TrackEventCommandHandler : IRequestHandler<TrackEventCommand, Result<bool>>
{
    private readonly IEventBuffer _buffer;

    public TrackEventCommandHandler(IEventBuffer buffer)
    {
        _buffer = buffer;
    }

    public async Task<Result<bool>> Handle(TrackEventCommand request, CancellationToken cancellationToken)
    {
        var @event = new ClickstreamEvent(
            request.EventName,
            request.UserId,
            request.SessionId,
            request.OccurredAt,
            request.Metadata);

        await _buffer.EnqueueAsync(@event, cancellationToken);

        return Result<bool>.Success(true);
    }
}
