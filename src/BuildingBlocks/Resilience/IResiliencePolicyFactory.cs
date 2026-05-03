using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Haworks.BuildingBlocks.Resilience;

/// <summary>
/// Options for configuring resilience policies.
/// All timing values are explicit to avoid hidden configurations.
/// </summary>
public record ResilienceOptions
{
    /// <summary>
    /// Name of the service these options apply to. Used for metrics and logging.
    /// </summary>
    public string ServiceName { get; init; } = "Default";

    /// <summary>Maximum number of retry attempts before giving up.</summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>Initial delay before first retry in milliseconds. Doubles with each attempt.</summary>
    public double InitialRetryDelayMs { get; init; } = 200;

    /// <summary>Maximum jitter to add to retry delays in milliseconds. 0 = no jitter.</summary>
    public double MaxJitterMs { get; init; } = 100;

    /// <summary>Number of exceptions before circuit breaker opens.</summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>How long the circuit stays open in seconds.</summary>
    public double CircuitBreakerDurationSeconds { get; init; } = 30;

    /// <summary>
    /// Optional bulkhead isolation settings. If null, no bulkhead is applied.
    /// Use bulkhead to limit concurrent executions and prevent resource exhaustion.
    /// </summary>
    public BulkheadOptions? Bulkhead { get; init; }

    /// <summary>
    /// Timeout in seconds for individual operations. 0 means no timeout.
    /// This is the per-operation timeout, not the total time including retries.
    /// </summary>
    public double TimeoutSeconds { get; init; } = 30;

    /// <summary>Default options with no bulkhead.</summary>
    public static ResilienceOptions Default => new();

    /// <summary>Pre-configured options for Stripe API calls.</summary>
    public static ResilienceOptions Stripe => new()
    {
        ServiceName = "Stripe",
        MaxRetryAttempts = 3,
        InitialRetryDelayMs = 1000,  // Stripe recommends 1s initial delay
        MaxJitterMs = 100,
        CircuitBreakerThreshold = 5,
        CircuitBreakerDurationSeconds = 30,
        TimeoutSeconds = 30,  // Stripe API can take up to 30s for some operations
        Bulkhead = BulkheadOptions.PaymentProvider
    };

    /// <summary>Pre-configured options for Vault API calls.</summary>
    public static ResilienceOptions Vault => new()
    {
        ServiceName = "Vault",
        MaxRetryAttempts = 5,
        InitialRetryDelayMs = 200,
        MaxJitterMs = 50,
        CircuitBreakerThreshold = 5,
        CircuitBreakerDurationSeconds = 30,
        TimeoutSeconds = 10,  // Vault operations should be fast
        Bulkhead = BulkheadOptions.SecretsManagement
    };

    /// <summary>Pre-configured options for storage operations (Minio, S3, etc.).</summary>
    public static ResilienceOptions Storage => new()
    {
        ServiceName = "Storage",
        MaxRetryAttempts = 3,
        InitialRetryDelayMs = 200,
        MaxJitterMs = 50,
        CircuitBreakerThreshold = 5,
        CircuitBreakerDurationSeconds = 30,
        TimeoutSeconds = 60,  // File uploads can take longer
        Bulkhead = BulkheadOptions.Storage
    };

    /// <summary>Pre-configured options for PayPal API calls.</summary>
    public static ResilienceOptions PayPal => new()
    {
        ServiceName = "PayPal",
        MaxRetryAttempts = 3,
        InitialRetryDelayMs = 500,  // PayPal recommends longer initial delay
        MaxJitterMs = 200,
        CircuitBreakerThreshold = 5,
        CircuitBreakerDurationSeconds = 60,  // Longer break for PayPal
        TimeoutSeconds = 45,  // PayPal API can be slower than Stripe
        Bulkhead = BulkheadOptions.PaymentProvider
    };

    /// <summary>
    /// Creates options for a custom external API service.
    /// </summary>
    /// <param name="serviceName">The name of the service for metrics and logging.</param>
    /// <param name="includeBulkhead">Whether to include default bulkhead isolation.</param>
    public static ResilienceOptions ForExternalApi(string serviceName, bool includeBulkhead = true) => new()
    {
        ServiceName = serviceName,
        MaxRetryAttempts = 3,
        InitialRetryDelayMs = 500,
        MaxJitterMs = 200,
        CircuitBreakerThreshold = 5,
        CircuitBreakerDurationSeconds = 30,
        TimeoutSeconds = 30,
        Bulkhead = includeBulkhead ? BulkheadOptions.Default : null
    };

    /// <summary>
    /// Creates options optimized for payment providers.
    /// </summary>
    /// <param name="serviceName">The name of the payment provider.</param>
    public static ResilienceOptions ForPaymentProvider(string serviceName) => new()
    {
        ServiceName = serviceName,
        MaxRetryAttempts = 3,
        InitialRetryDelayMs = 1000,
        MaxJitterMs = 100,
        CircuitBreakerThreshold = 5,
        CircuitBreakerDurationSeconds = 30,
        TimeoutSeconds = 30,
        Bulkhead = BulkheadOptions.PaymentProvider
    };
}

/// <summary>
/// Factory for creating Polly resilience policies.
/// Provides centralized configuration for retry and circuit breaker patterns.
///
/// Usage:
/// <code>
/// // For void operations:
/// var policy = _factory.CreateCombinedPolicy(ResilienceOptions.Stripe);
/// await policy.ExecuteAsync(() => DoSomethingAsync());
///
/// // For operations returning a value:
/// var policy = _factory.CreateRetryPolicy&lt;MyResult&gt;(ResilienceOptions.Stripe);
/// var result = await policy.ExecuteAsync(() => GetSomethingAsync());
/// </code>
/// </summary>
public interface IResiliencePolicyFactory
{
    /// <summary>
    /// Creates a circuit breaker policy for the specified operation type.
    /// Opens after threshold consecutive failures, stays open for duration.
    /// </summary>
    AsyncCircuitBreakerPolicy CreateCircuitBreaker(
        ResilienceOptions options,
        Action<Exception, TimeSpan>? onBreak = null,
        Action? onReset = null);

    /// <summary>
    /// Creates a retry policy with exponential backoff and optional jitter.
    /// Delay formula: InitialRetryDelayMs * 2^(attempt-1) + random(0, MaxJitterMs)
    /// </summary>
    AsyncRetryPolicy CreateRetryPolicy(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null);

    /// <summary>
    /// Creates a typed retry policy for operations that return a value.
    /// Handles both exceptions and optionally invalid results.
    /// </summary>
    AsyncRetryPolicy<TResult> CreateRetryPolicy<TResult>(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null,
        Func<TResult, bool>? shouldRetryResult = null);

    /// <summary>
    /// Creates a combined policy (retry wrapped with circuit breaker).
    /// Circuit breaker prevents retries when the circuit is open.
    /// </summary>
    IAsyncPolicy CreateCombinedPolicy(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null,
        Action<Exception, TimeSpan>? onBreak = null,
        Action? onReset = null);

    /// <summary>
    /// Creates a typed combined policy for operations that return a value.
    /// </summary>
    IAsyncPolicy<TResult> CreateCombinedPolicy<TResult>(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null,
        Action<Exception, TimeSpan>? onBreak = null,
        Action? onReset = null,
        Func<TResult, bool>? shouldRetryResult = null);

    // ============================================================================
    // NEW: Enhanced policy creation with metrics, bulkhead, and fallback support
    // ============================================================================

    /// <summary>
    /// Creates a comprehensive policy with retry, circuit breaker, optional bulkhead, and metrics.
    /// Policy execution order: Bulkhead (if configured) -> CircuitBreaker -> Retry
    /// </summary>
    /// <param name="options">The resilience options including optional bulkhead configuration.</param>
    /// <returns>A combined async policy.</returns>
    IAsyncPolicy CreatePolicy(ResilienceOptions options);

    /// <summary>
    /// Creates a comprehensive typed policy with retry, circuit breaker, optional bulkhead,
    /// optional fallback, and metrics.
    /// </summary>
    /// <typeparam name="TResult">The result type of the operation.</typeparam>
    /// <param name="options">The resilience options including optional bulkhead configuration.</param>
    /// <param name="fallbackHandler">Optional fallback handler for graceful degradation.</param>
    /// <returns>A combined async policy with fallback support.</returns>
    IAsyncPolicy<TResult> CreatePolicy<TResult>(
        ResilienceOptions options,
        IFallbackHandler<TResult>? fallbackHandler = null);

    /// <summary>
    /// Creates a bulkhead isolation policy.
    /// </summary>
    /// <param name="options">The bulkhead options.</param>
    /// <param name="serviceName">The service name for metrics.</param>
    /// <returns>A bulkhead policy.</returns>
    IAsyncPolicy CreateBulkhead(BulkheadOptions options, string serviceName);

    /// <summary>
    /// Creates a typed bulkhead isolation policy.
    /// </summary>
    /// <typeparam name="TResult">The result type of the operation.</typeparam>
    /// <param name="options">The bulkhead options.</param>
    /// <param name="serviceName">The service name for metrics.</param>
    /// <returns>A typed bulkhead policy.</returns>
    IAsyncPolicy<TResult> CreateBulkhead<TResult>(BulkheadOptions options, string serviceName);
}
