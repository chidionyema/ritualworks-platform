using Haworks.BuildingBlocks.Resilience.Exceptions;
using Polly.CircuitBreaker;

namespace Haworks.BuildingBlocks.Resilience.Fallbacks;

/// <summary>
/// Never falls back - throws <see cref="CircuitBreakerOpenException"/> when circuit is open.
/// Use this for critical operations (payments, transactions) that must not auto-degrade.
/// </summary>
/// <typeparam name="TResult">The result type of the operation.</typeparam>
/// <remarks>
/// This handler ensures critical operations fail explicitly rather than returning stale or default data.
/// The thrown exception includes service name and estimated recovery time for proper error handling.
/// </remarks>
/// <example>
/// <code>
/// // For a payment processor that must not auto-degrade
/// var policy = policyFactory.CreatePolicy&lt;PaymentResult&gt;(
///     ResilienceOptions.ForPaymentProvider("Stripe"),
///     fallbackHandler: new CriticalOperationHandler&lt;PaymentResult&gt;("Stripe", TimeSpan.FromSeconds(30)));
///
/// // Throws CircuitBreakerOpenException when circuit is open
/// var result = await policy.ExecuteAsync(() => ProcessPaymentAsync());
/// </code>
/// </example>
public sealed class CriticalOperationHandler<TResult> : IFallbackHandler<TResult>
{
    private readonly string _serviceName;
    private readonly TimeSpan _circuitBreakerDuration;

    /// <summary>
    /// Creates a new instance of <see cref="CriticalOperationHandler{TResult}"/>.
    /// </summary>
    /// <param name="serviceName">The name of the service for error reporting.</param>
    /// <param name="circuitBreakerDuration">The circuit breaker duration for recovery time estimation.</param>
    public CriticalOperationHandler(string serviceName, TimeSpan circuitBreakerDuration)
    {
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _circuitBreakerDuration = circuitBreakerDuration;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always returns false - critical operations should never fall back automatically.
    /// The exception will be wrapped in <see cref="CircuitBreakerOpenException"/> via GetFallbackValueAsync.
    /// </remarks>
    public bool ShouldFallback(Exception exception) =>
        exception is BrokenCircuitException or IsolatedCircuitException;

    /// <inheritdoc />
    /// <remarks>
    /// Throws <see cref="CircuitBreakerOpenException"/> with service context.
    /// This allows upstream handlers to properly report service unavailability.
    /// </remarks>
    public Task<TResult> GetFallbackValueAsync(Exception exception, CancellationToken cancellationToken) =>
        throw new CircuitBreakerOpenException(_serviceName, _circuitBreakerDuration, exception);
}
