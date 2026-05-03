namespace Haworks.BuildingBlocks.Messaging;

/// <summary>
/// Abstraction for publishing cross-service integration events.
/// Enables handlers to publish events without depending on specific messaging infrastructure.
///
/// Usage in handlers:
/// <code>
/// await _eventPublisher.PublishAsync(new OrderCreatedEvent
/// {
///     OrderId = order.Id,
///     CustomerEmail = customerEmail,
///     SagaId = sagaId
/// }, cancellationToken);
/// </code>
///
/// Events are stored in the per-context outbox in the same transaction as
/// business state, then delivered asynchronously by the MassTransit
/// <c>BusOutboxDeliveryService</c>. See <see cref="BoundedContextConsumerDefinition{TConsumer, TDbContext}"/>
/// for the consume-side wiring that completes the symmetry.
/// </summary>
public interface IDomainEventPublisher
{
    /// <summary>
    /// Publishes a domain event. The event is stored in the outbox table
    /// within the current transaction and delivered asynchronously.
    /// </summary>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class;

    /// <summary>
    /// Publishes multiple domain events. All events are stored in the outbox
    /// within the current transaction.
    /// </summary>
    Task PublishBatchAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken ct = default)
        where TEvent : class;
}
