using System.Diagnostics.Metrics;

namespace Haworks.BuildingBlocks.Resilience;

/// <summary>
/// Resilience metrics implementation using System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry and other metrics collectors.
/// </summary>
public sealed class ResilienceMetrics : IResilienceMetrics
{
    /// <summary>
    /// The meter name used for all resilience metrics.
    /// </summary>
    public const string MeterName = "Resilience.Policies";

    private readonly Counter<long> _retryAttempts;
    private readonly Counter<long> _circuitBreakerStateChanges;
    private readonly Counter<long> _circuitBreakerRejections;
    private readonly Histogram<double> _operationDuration;
    private readonly Counter<long> _bulkheadRejections;
    private readonly Counter<long> _fallbackExecutions;
    private readonly Counter<long> _timeouts;

    /// <summary>
    /// Creates a new instance of <see cref="ResilienceMetrics"/>.
    /// </summary>
    /// <param name="meterFactory">The meter factory for creating metrics instruments.</param>
    public ResilienceMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName, "1.0.0");

        _retryAttempts = meter.CreateCounter<long>(
            name: "resilience.retry.attempts",
            unit: "{attempt}",
            description: "Number of retry attempts made");

        _circuitBreakerStateChanges = meter.CreateCounter<long>(
            name: "resilience.circuit_breaker.state_changes",
            unit: "{transition}",
            description: "Number of circuit breaker state transitions");

        _circuitBreakerRejections = meter.CreateCounter<long>(
            name: "resilience.circuit_breaker.rejections",
            unit: "{rejection}",
            description: "Number of requests rejected by open circuit breaker");

        _operationDuration = meter.CreateHistogram<double>(
            name: "resilience.operation.duration",
            unit: "ms",
            description: "Duration of resilient operations in milliseconds");

        _bulkheadRejections = meter.CreateCounter<long>(
            name: "resilience.bulkhead.rejections",
            unit: "{rejection}",
            description: "Number of requests rejected by bulkhead at capacity");

        _fallbackExecutions = meter.CreateCounter<long>(
            name: "resilience.fallback.executions",
            unit: "{execution}",
            description: "Number of fallback executions");

        _timeouts = meter.CreateCounter<long>(
            name: "resilience.timeout.occurrences",
            unit: "{timeout}",
            description: "Number of operation timeouts");
    }

    /// <inheritdoc />
    public void RecordRetryAttempt(string serviceName, int attemptNumber, string exceptionType)
    {
        _retryAttempts.Add(1,
            new KeyValuePair<string, object?>("service", serviceName),
            new KeyValuePair<string, object?>("attempt", attemptNumber),
            new KeyValuePair<string, object?>("exception_type", exceptionType));
    }

    /// <inheritdoc />
    public void RecordCircuitBreakerStateChange(string serviceName, CircuitBreakerState newState)
    {
        _circuitBreakerStateChanges.Add(1,
            new KeyValuePair<string, object?>("service", serviceName),
            new KeyValuePair<string, object?>("state", newState.ToString().ToLowerInvariant()));
    }

    /// <inheritdoc />
    public void RecordCircuitBreakerRejection(string serviceName)
    {
        _circuitBreakerRejections.Add(1,
            new KeyValuePair<string, object?>("service", serviceName));
    }

    /// <inheritdoc />
    public void RecordOperationDuration(string serviceName, double durationMs, bool success)
    {
        _operationDuration.Record(durationMs,
            new KeyValuePair<string, object?>("service", serviceName),
            new KeyValuePair<string, object?>("success", success));
    }

    /// <inheritdoc />
    public void RecordBulkheadRejection(string serviceName)
    {
        _bulkheadRejections.Add(1,
            new KeyValuePair<string, object?>("service", serviceName));
    }

    /// <inheritdoc />
    public void RecordFallbackExecuted(string serviceName, string reason)
    {
        _fallbackExecutions.Add(1,
            new KeyValuePair<string, object?>("service", serviceName),
            new KeyValuePair<string, object?>("reason", reason));
    }

    /// <inheritdoc />
    public void RecordTimeout(string serviceName)
    {
        _timeouts.Add(1,
            new KeyValuePair<string, object?>("service", serviceName));
    }
}
