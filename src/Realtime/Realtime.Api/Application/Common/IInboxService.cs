using Haworks.BuildingBlocks.Common;

namespace Haworks.Realtime.Api.Application.Common;

public interface IInboxService
{
    Task StoreMessageAsync(Guid userId, object message, CancellationToken ct = default);
    Task<IEnumerable<object>> GetAndClearMessagesAsync(Guid userId, CancellationToken ct = default);
}
