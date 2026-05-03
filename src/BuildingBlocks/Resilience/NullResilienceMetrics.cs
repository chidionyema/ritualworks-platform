namespace Haworks.BuildingBlocks.Resilience;

/// <summary>
/// No-op implementation of <see cref="IResilienceMetrics"/>.
/// Use when metrics collection is disabled or not configured.
/// </summary>
public sealed class NullResilienceMetrics : IResilienceMetrics
{
    /// <summary>
    /// Singleton instance for efficient reuse.
    /// </summary>
    public static readonly IResilienceMetrics Instance = new NullResilienceMetrics();

    private NullResilienceMetrics() { }

    /// <inheritdoc />
    public void RecordRetryAttempt(string serviceName, int attemptNumber, string exceptionType) { }

    /// <inheritdoc />
    public void RecordCircuitBreakerStateChange(string serviceName, CircuitBreakerState newState) { }

    /// <inheritdoc />
    public void RecordCircuitBreakerRejection(string serviceName) { }

    /// <inheritdoc />
    public void RecordOperationDuration(string serviceName, double durationMs, bool success) { }

    /// <inheritdoc />
    public void RecordBulkheadRejection(string serviceName) { }

    /// <inheritdoc />
    public void RecordFallbackExecuted(string serviceName, string reason) { }

    /// <inheritdoc />
    public void RecordTimeout(string serviceName) { }
}
