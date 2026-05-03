namespace Haworks.BuildingBlocks.Resilience;

/// <summary>
/// Defines fallback behavior when a primary operation fails due to resilience policies.
/// Implement this interface to provide custom fallback logic for different scenarios.
/// </summary>
/// <typeparam name="TResult">The result type of the operation.</typeparam>
public interface IFallbackHandler<TResult>
{
    /// <summary>
    /// Determines whether a fallback should be executed for the given exception.
    /// </summary>
    /// <param name="exception">The exception that caused the operation to fail.</param>
    /// <returns>True if fallback should be executed; false to let the exception propagate.</returns>
    bool ShouldFallback(Exception exception);

    /// <summary>
    /// Gets the fallback value when the primary operation fails.
    /// </summary>
    /// <param name="exception">The exception that caused the operation to fail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fallback value.</returns>
    Task<TResult> GetFallbackValueAsync(Exception exception, CancellationToken cancellationToken);
}
