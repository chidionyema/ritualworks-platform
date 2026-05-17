using Haworks.Contracts.Orders;
using Haworks.Realtime.Api.Application.Notifications;
using MassTransit;
using MediatR;

namespace Haworks.Realtime.Api.Infrastructure.Messaging;

public class OrderStatusChangedConsumer : IConsumer<OrderStatusChanged>
{
    private readonly ISender _sender;
    private readonly ILogger<OrderStatusChangedConsumer> _logger;

    public OrderStatusChangedConsumer(ISender sender, ILogger<OrderStatusChangedConsumer> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<OrderStatusChanged> context)
    {
        var @event = context.Message;
        _logger.LogInformation("Consumed OrderStatusChanged event for Order {OrderId}", @event.OrderId);

        var command = new SendNotificationCommand
        {
            UserId = @event.CustomerId,
            MessageType = "OrderStatusChanged",
            Data = new
            {
                @event.OrderId,
                @event.NewStatus,
                @event.ChangedAt
            }
        };

        return _sender.Send(command);
    }
}
