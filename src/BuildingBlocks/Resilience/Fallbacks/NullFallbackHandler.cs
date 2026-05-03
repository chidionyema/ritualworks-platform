using Polly.CircuitBreaker;

namespace Haworks.BuildingBlocks.Resilience.Fallbacks;

/// <summary>
/// Returns default(TResult) when the circuit breaker is open.
/// Use this for non-critical read operations where returning null/empty is acceptable.
/// </summary>
/// <typeparam name="TResult">The result type of the operation.</typeparam>
/// <example>
/// <code>
/// // For a catalog service that can gracefully degrade
/// var policy = policyFactory.CreatePolicy&lt;List&lt;Product&gt;&gt;(
///     options,
///     fallbackHandler: NullFallbackHandler&lt;List&lt;Product&gt;&gt;.Instance);
///
/// // Returns empty list when circuit is open
/// var products = await policy.ExecuteAsync(() => FetchProductsAsync());
/// </code>
/// </example>
public sealed class NullFallbackHandler<TResult> : IFallbackHandler<TResult>
{
    /// <summary>
    /// Singleton instance for efficient reuse.
    /// </summary>
    public static readonly NullFallbackHandler<TResult> Instance = new();

    private NullFallbackHandler() { }

    /// <inheritdoc />
    /// <remarks>
    /// Returns true only for circuit breaker exceptions (BrokenCircuitException, IsolatedCircuitException).
    /// Other exceptions will propagate normally.
    /// </remarks>
    public bool ShouldFallback(Exception exception) =>
        exception is BrokenCircuitException or IsolatedCircuitException;

    /// <inheritdoc />
    /// <remarks>
    /// Returns default(TResult), which is null for reference types and default value for value types.
    /// Consider using a typed fallback handler if you need a specific default value.
    /// </remarks>
    public Task<TResult> GetFallbackValueAsync(Exception exception, CancellationToken cancellationToken) =>
        Task.FromResult(default(TResult)!);
}
