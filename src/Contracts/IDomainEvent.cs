namespace Haworks.Contracts;

/// <summary>
/// Marker interface for cross-service integration events.
/// All events published via <c>Haworks.BuildingBlocks.Messaging.IDomainEventPublisher</c>
/// implement this interface. (Cref deliberately a string — Contracts must not
/// depend on BuildingBlocks; the dependency is one-way the other direction.)
/// </summary>
public interface IDomainEvent
{
    /// <summary>Unique identifier for this event instance. Used for inbox dedupe (`MessageId`).</summary>
    Guid EventId { get; }

    /// <summary>When the event occurred. Producer's clock.</summary>
    DateTime OccurredAt { get; }
}

/// <summary>
/// Base record for cross-service integration events.
/// Provides default <see cref="EventId"/> and <see cref="OccurredAt"/>.
/// All concrete events should also carry a <c>SagaId</c> when participating in saga choreographies.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <inheritdoc />
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
