namespace Haworks.BuildingBlocks.Resilience.Exceptions;

/// <summary>
/// Thrown when a critical operation cannot proceed because the circuit breaker is open.
/// This exception signals that the upstream service is unavailable and requests should not be attempted.
/// </summary>
/// <remarks>
/// Use this exception for critical operations (e.g., payments) that should not have automatic fallback behavior.
/// The estimated recovery time can be used by callers to implement retry-after logic.
/// </remarks>
public sealed class CircuitBreakerOpenException : Exception
{
    /// <summary>
    /// Gets the name of the service that is unavailable.
    /// </summary>
    public string ServiceName { get; }

    /// <summary>
    /// Gets the estimated time until the circuit breaker may allow requests again.
    /// </summary>
    public TimeSpan EstimatedRecoveryTime { get; }

    /// <summary>
    /// Creates a new instance of <see cref="CircuitBreakerOpenException"/>.
    /// </summary>
    /// <param name="serviceName">The name of the unavailable service.</param>
    /// <param name="estimatedRecoveryTime">The estimated time until recovery.</param>
    public CircuitBreakerOpenException(string serviceName, TimeSpan estimatedRecoveryTime)
        : base($"Service '{serviceName}' is unavailable. Estimated recovery: {estimatedRecoveryTime.TotalSeconds:F0}s")
    {
        ServiceName = serviceName;
        EstimatedRecoveryTime = estimatedRecoveryTime;
    }

    /// <summary>
    /// Creates a new instance with an inner exception.
    /// </summary>
    /// <param name="serviceName">The name of the unavailable service.</param>
    /// <param name="estimatedRecoveryTime">The estimated time until recovery.</param>
    /// <param name="innerException">The exception that caused the circuit to open.</param>
    public CircuitBreakerOpenException(string serviceName, TimeSpan estimatedRecoveryTime, Exception innerException)
        : base($"Service '{serviceName}' is unavailable. Estimated recovery: {estimatedRecoveryTime.TotalSeconds:F0}s", innerException)
    {
        ServiceName = serviceName;
        EstimatedRecoveryTime = estimatedRecoveryTime;
    }
}
