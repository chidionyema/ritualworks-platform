namespace Haworks.Contracts;

/// <summary>
/// Marker interface for cross-service integration events.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTime OccurredAt { get; }
}
