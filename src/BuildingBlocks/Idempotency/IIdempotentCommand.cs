namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// Marker interface for commands that require idempotency enforcement.
/// Commands implementing this interface are intercepted by <see cref="IdempotencyBehavior{TRequest,TResponse}"/>
/// which checks/records execution in the IdempotencyJournal before invoking the handler.
///
/// Any command that mutates state (creates resources, calls external APIs, publishes events)
/// MUST implement this interface. Read-only queries do not need it.
/// </summary>
public interface IIdempotentCommand
{
    /// <summary>
    /// Client-generated key that uniquely identifies this command invocation.
    /// If the same key is seen twice, the cached response is returned without re-executing.
    /// </summary>
    string IdempotencyKey { get; }
}
