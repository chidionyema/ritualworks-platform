namespace Haworks.Webhooks.Application.Interfaces;

public interface IWebhookDispatcher
{
    Task DispatchAsync(Guid deliveryId, CancellationToken ct);
}
