namespace Haworks.BuildingBlocks.Resilience;

/// <summary>
/// Records metrics for resilience patterns (retry, circuit breaker, bulkhead, fallback).
/// Implement this interface to integrate with your preferred metrics system.
/// </summary>
public interface IResilienceMetrics
{
    /// <summary>
    /// Records a retry attempt with service context.
    /// </summary>
    /// <param name="serviceName">The name of the service being called.</param>
    /// <param name="attemptNumber">The retry attempt number (1-based).</param>
    /// <param name="exceptionType">The type name of the exception that triggered the retry.</param>
    void RecordRetryAttempt(string serviceName, int attemptNumber, string exceptionType);

    /// <summary>
    /// Records a circuit breaker state change.
    /// </summary>
    /// <param name="serviceName">The name of the service.</param>
    /// <param name="newState">The new circuit breaker state.</param>
    void RecordCircuitBreakerStateChange(string serviceName, CircuitBreakerState newState);

    /// <summary>
    /// Records when a circuit breaker rejects a request because it's open.
    /// </summary>
    /// <param name="serviceName">The name of the service.</param>
    void RecordCircuitBreakerRejection(string serviceName);

    /// <summary>
    /// Records the duration of a resilient operation.
    /// </summary>
    /// <param name="serviceName">The name of the service.</param>
    /// <param name="durationMs">The duration in milliseconds.</param>
    /// <param name="success">Whether the operation succeeded.</param>
    void RecordOperationDuration(string serviceName, double durationMs, bool success);

    /// <summary>
    /// Records when a bulkhead rejects a request due to capacity limits.
    /// </summary>
    /// <param name="serviceName">The name of the service.</param>
    void RecordBulkheadRejection(string serviceName);

    /// <summary>
    /// Records when a fallback is executed.
    /// </summary>
    /// <param name="serviceName">The name of the service.</param>
    /// <param name="reason">The reason for the fallback (e.g., exception type).</param>
    void RecordFallbackExecuted(string serviceName, string reason);

    /// <summary>
    /// Records when an operation times out.
    /// </summary>
    /// <param name="serviceName">The name of the service.</param>
    void RecordTimeout(string serviceName);
}

/// <summary>
/// Represents the state of a circuit breaker.
/// </summary>
public enum CircuitBreakerState
{
    /// <summary>Circuit is closed - requests flow normally.</summary>
    Closed,

    /// <summary>Circuit is open - requests are rejected immediately.</summary>
    Open,

    /// <summary>Circuit is half-open - testing if service has recovered.</summary>
    HalfOpen
}
