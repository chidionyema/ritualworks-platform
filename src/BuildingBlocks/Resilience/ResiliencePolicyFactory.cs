using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Bulkhead;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Haworks.BuildingBlocks.Resilience;

/// <summary>
/// Creates Polly resilience policies for external service operations.
/// Implements retry with exponential backoff, circuit breaker, bulkhead isolation,
/// and optional fallback patterns with comprehensive metrics.
///
/// All configuration is explicit via ResilienceOptions - no hidden magic numbers.
/// </summary>
public sealed class ResiliencePolicyFactory : IResiliencePolicyFactory
{
    private readonly IResilienceMetrics _metrics;
    private readonly ILogger<ResiliencePolicyFactory> _logger;

    /// <summary>
    /// Creates a new instance with optional metrics and logging.
    /// </summary>
    /// <param name="metrics">Resilience metrics recorder. Defaults to NullResilienceMetrics if not provided.</param>
    /// <param name="logger">Logger for policy events. Defaults to NullLogger if not provided.</param>
    public ResiliencePolicyFactory(
        IResilienceMetrics? metrics = null,
        ILogger<ResiliencePolicyFactory>? logger = null)
    {
        _metrics = metrics ?? NullResilienceMetrics.Instance;
        _logger = logger ?? NullLogger<ResiliencePolicyFactory>.Instance;
    }

    // ============================================================================
    // NEW: Enhanced policy creation with metrics, bulkhead, and fallback
    // ============================================================================

    /// <inheritdoc />
    public IAsyncPolicy CreatePolicy(ResilienceOptions options)
    {
        var serviceName = options.ServiceName;

        var retryPolicy = CreateRetryPolicyInternal(options, serviceName);
        var circuitBreaker = CreateCircuitBreakerInternal(options, serviceName);

        // Wrap order: Bulkhead (outer) -> CircuitBreaker -> Retry -> Timeout (inner)
        // Timeout is innermost so it applies to each individual operation attempt
        IAsyncPolicy combined = Policy.WrapAsync(circuitBreaker, retryPolicy);

        // Add timeout policy if configured (> 0)
        if (options.TimeoutSeconds > 0)
        {
            var timeoutPolicy = CreateTimeoutPolicyInternal(options, serviceName);
            combined = Policy.WrapAsync(combined, timeoutPolicy);
        }

        if (options.Bulkhead is not null)
        {
            var bulkhead = CreateBulkheadInternal(options.Bulkhead, serviceName);
            combined = Policy.WrapAsync(bulkhead, combined);
        }

        return combined;
    }

    /// <inheritdoc />
    public IAsyncPolicy<TResult> CreatePolicy<TResult>(
        ResilienceOptions options,
        IFallbackHandler<TResult>? fallbackHandler = null)
    {
        var serviceName = options.ServiceName;

        var retryPolicy = CreateRetryPolicyInternal<TResult>(options, serviceName);
        var circuitBreaker = CreateCircuitBreakerInternal<TResult>(options, serviceName);

        // Wrap order: Fallback (outer) -> Bulkhead -> CircuitBreaker -> Retry -> Timeout (inner)
        // Timeout is innermost so it applies to each individual operation attempt
        IAsyncPolicy<TResult> combined = Policy.WrapAsync(circuitBreaker, retryPolicy);

        // Add timeout policy if configured (> 0)
        if (options.TimeoutSeconds > 0)
        {
            var timeoutPolicy = CreateTimeoutPolicyInternal<TResult>(options, serviceName);
            combined = Policy.WrapAsync(combined, timeoutPolicy);
        }

        if (options.Bulkhead is not null)
        {
            var bulkhead = CreateBulkheadInternal<TResult>(options.Bulkhead, serviceName);
            combined = Policy.WrapAsync(bulkhead, combined);
        }

        if (fallbackHandler is not null)
        {
            var fallbackPolicy = CreateFallbackPolicy(fallbackHandler, serviceName);
            combined = Policy.WrapAsync(fallbackPolicy, combined);
        }

        return combined;
    }

    /// <inheritdoc />
    public IAsyncPolicy CreateBulkhead(BulkheadOptions options, string serviceName)
    {
        return CreateBulkheadInternal(options, serviceName);
    }

    /// <inheritdoc />
    public IAsyncPolicy<TResult> CreateBulkhead<TResult>(BulkheadOptions options, string serviceName)
    {
        return CreateBulkheadInternal<TResult>(options, serviceName);
    }

    // ============================================================================
    // EXISTING: Backward-compatible methods (no breaking changes)
    // ============================================================================

    /// <inheritdoc />
    public AsyncCircuitBreakerPolicy CreateCircuitBreaker(
        ResilienceOptions options,
        Action<Exception, TimeSpan>? onBreak = null,
        Action? onReset = null)
    {
        return Policy
            .Handle<Exception>(IsTransient)
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: options.CircuitBreakerThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds),
                onBreak: (ex, duration) =>
                {
                    _logger.LogWarning(ex, "Circuit breaker opened for {Duration}s", duration.TotalSeconds);
                    _metrics.RecordCircuitBreakerStateChange(options.ServiceName, CircuitBreakerState.Open);
                    onBreak?.Invoke(ex, duration);
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit breaker reset");
                    _metrics.RecordCircuitBreakerStateChange(options.ServiceName, CircuitBreakerState.Closed);
                    onReset?.Invoke();
                },
                onHalfOpen: () =>
                {
                    _metrics.RecordCircuitBreakerStateChange(options.ServiceName, CircuitBreakerState.HalfOpen);
                });
    }

    /// <inheritdoc />
    public AsyncRetryPolicy CreateRetryPolicy(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null)
    {
        return Policy
            .Handle<Exception>(IsTransient)
            .WaitAndRetryAsync(
                options.MaxRetryAttempts,
                retryAttempt => CalculateDelay(options, retryAttempt),
                onRetry: (ex, delay, retryCount, _) =>
                {
                    _logger.LogWarning(ex,
                        "Retry attempt {RetryCount}/{MaxRetries} after {Delay}ms for {Service}",
                        retryCount, options.MaxRetryAttempts, delay.TotalMilliseconds, options.ServiceName);
                    _metrics.RecordRetryAttempt(options.ServiceName, retryCount, ex.GetType().Name);
                    onRetry?.Invoke(ex, delay, retryCount);
                });
    }

    /// <inheritdoc />
    public AsyncRetryPolicy<TResult> CreateRetryPolicy<TResult>(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null,
        Func<TResult, bool>? shouldRetryResult = null)
    {
        var policyBuilder = Policy<TResult>.Handle<Exception>(IsTransient);

        if (shouldRetryResult != null)
        {
            policyBuilder = policyBuilder.OrResult(shouldRetryResult);
        }

        return policyBuilder.WaitAndRetryAsync(
            options.MaxRetryAttempts,
            retryAttempt => CalculateDelay(options, retryAttempt),
            onRetry: (outcome, delay, retryCount, _) =>
            {
                if (outcome.Exception != null)
                {
                    _logger.LogWarning(outcome.Exception,
                        "Retry attempt {RetryCount}/{MaxRetries} after {Delay}ms for {Service}",
                        retryCount, options.MaxRetryAttempts, delay.TotalMilliseconds, options.ServiceName);
                    _metrics.RecordRetryAttempt(options.ServiceName, retryCount, outcome.Exception.GetType().Name);
                    onRetry?.Invoke(outcome.Exception, delay, retryCount);
                }
                else
                {
                    _logger.LogWarning(
                        "Retry attempt {RetryCount}/{MaxRetries} after {Delay}ms for {Service} due to result condition",
                        retryCount, options.MaxRetryAttempts, delay.TotalMilliseconds, options.ServiceName);
                    _metrics.RecordRetryAttempt(options.ServiceName, retryCount, "ResultCondition");
                }
            });
    }

    /// <inheritdoc />
    public IAsyncPolicy CreateCombinedPolicy(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null,
        Action<Exception, TimeSpan>? onBreak = null,
        Action? onReset = null)
    {
        var retryPolicy = CreateRetryPolicy(options, onRetry);
        var circuitBreaker = CreateCircuitBreaker(options, onBreak, onReset);

        return Policy.WrapAsync(circuitBreaker, retryPolicy);
    }

    /// <inheritdoc />
    public IAsyncPolicy<TResult> CreateCombinedPolicy<TResult>(
        ResilienceOptions options,
        Action<Exception, TimeSpan, int>? onRetry = null,
        Action<Exception, TimeSpan>? onBreak = null,
        Action? onReset = null,
        Func<TResult, bool>? shouldRetryResult = null)
    {
        var retryPolicy = CreateRetryPolicy<TResult>(options, onRetry, shouldRetryResult);

        var circuitBreaker = Policy<TResult>
            .Handle<Exception>(IsTransient)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.CircuitBreakerThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds),
                onBreak: (outcome, duration) =>
                {
                    _logger.LogWarning(outcome.Exception,
                        "Circuit breaker opened for {Duration}s", duration.TotalSeconds);
                    _metrics.RecordCircuitBreakerStateChange(options.ServiceName, CircuitBreakerState.Open);
                    onBreak?.Invoke(outcome.Exception!, duration);
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit breaker reset");
                    _metrics.RecordCircuitBreakerStateChange(options.ServiceName, CircuitBreakerState.Closed);
                    onReset?.Invoke();
                },
                onHalfOpen: () =>
                {
                    _metrics.RecordCircuitBreakerStateChange(options.ServiceName, CircuitBreakerState.HalfOpen);
                });

        return Policy.WrapAsync(circuitBreaker, retryPolicy);
    }

    // ============================================================================
    // INTERNAL: Policy creation with metrics
    // ============================================================================

    private AsyncRetryPolicy CreateRetryPolicyInternal(ResilienceOptions options, string serviceName)
    {
        return Policy
            .Handle<Exception>(IsTransient)
            .WaitAndRetryAsync(
                options.MaxRetryAttempts,
                retryAttempt => CalculateDelay(options, retryAttempt),
                onRetry: (ex, delay, retryCount, _) =>
                {
                    _logger.LogWarning(ex,
                        "Retry attempt {RetryCount}/{MaxRetries} after {Delay}ms for {Service}",
                        retryCount, options.MaxRetryAttempts, delay.TotalMilliseconds, serviceName);
                    _metrics.RecordRetryAttempt(serviceName, retryCount, ex.GetType().Name);
                });
    }

    private AsyncRetryPolicy<TResult> CreateRetryPolicyInternal<TResult>(ResilienceOptions options, string serviceName)
    {
        return Policy<TResult>
            .Handle<Exception>(IsTransient)
            .WaitAndRetryAsync(
                options.MaxRetryAttempts,
                retryAttempt => CalculateDelay(options, retryAttempt),
                onRetry: (outcome, delay, retryCount, _) =>
                {
                    var exceptionType = outcome.Exception?.GetType().Name ?? "ResultCondition";
                    _logger.LogWarning(outcome.Exception,
                        "Retry attempt {RetryCount}/{MaxRetries} after {Delay}ms for {Service}",
                        retryCount, options.MaxRetryAttempts, delay.TotalMilliseconds, serviceName);
                    _metrics.RecordRetryAttempt(serviceName, retryCount, exceptionType);
                });
    }

    private AsyncCircuitBreakerPolicy CreateCircuitBreakerInternal(ResilienceOptions options, string serviceName)
    {
        return Policy
            .Handle<Exception>(IsTransient)
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: options.CircuitBreakerThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds),
                onBreak: (ex, duration) =>
                {
                    _logger.LogWarning(ex, "Circuit breaker opened for {Duration}s on {Service}",
                        duration.TotalSeconds, serviceName);
                    _metrics.RecordCircuitBreakerStateChange(serviceName, CircuitBreakerState.Open);
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit breaker reset for {Service}", serviceName);
                    _metrics.RecordCircuitBreakerStateChange(serviceName, CircuitBreakerState.Closed);
                },
                onHalfOpen: () =>
                {
                    _metrics.RecordCircuitBreakerStateChange(serviceName, CircuitBreakerState.HalfOpen);
                });
    }

    private AsyncCircuitBreakerPolicy<TResult> CreateCircuitBreakerInternal<TResult>(
        ResilienceOptions options, string serviceName)
    {
        return Policy<TResult>
            .Handle<Exception>(IsTransient)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.CircuitBreakerThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds),
                onBreak: (outcome, duration) =>
                {
                    _logger.LogWarning(outcome.Exception,
                        "Circuit breaker opened for {Duration}s on {Service}",
                        duration.TotalSeconds, serviceName);
                    _metrics.RecordCircuitBreakerStateChange(serviceName, CircuitBreakerState.Open);
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit breaker reset for {Service}", serviceName);
                    _metrics.RecordCircuitBreakerStateChange(serviceName, CircuitBreakerState.Closed);
                },
                onHalfOpen: () =>
                {
                    _metrics.RecordCircuitBreakerStateChange(serviceName, CircuitBreakerState.HalfOpen);
                });
    }

    private AsyncBulkheadPolicy CreateBulkheadInternal(BulkheadOptions options, string serviceName)
    {
        return Policy.BulkheadAsync(
            maxParallelization: options.MaxParallelization,
            maxQueuingActions: options.MaxQueuingActions,
            onBulkheadRejectedAsync: _ =>
            {
                _logger.LogWarning("Bulkhead rejected request for {Service}", serviceName);
                _metrics.RecordBulkheadRejection(serviceName);
                return Task.CompletedTask;
            });
    }

    private AsyncBulkheadPolicy<TResult> CreateBulkheadInternal<TResult>(BulkheadOptions options, string serviceName)
    {
        return Policy.BulkheadAsync<TResult>(
            maxParallelization: options.MaxParallelization,
            maxQueuingActions: options.MaxQueuingActions,
            onBulkheadRejectedAsync: _ =>
            {
                _logger.LogWarning("Bulkhead rejected request for {Service}", serviceName);
                _metrics.RecordBulkheadRejection(serviceName);
                return Task.CompletedTask;
            });
    }

    private AsyncTimeoutPolicy CreateTimeoutPolicyInternal(ResilienceOptions options, string serviceName)
    {
        return Policy.TimeoutAsync(
            timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
            timeoutStrategy: TimeoutStrategy.Optimistic,
            onTimeoutAsync: (context, timeout, task) =>
            {
                _logger.LogWarning(
                    "Operation timed out for {Service} after {Timeout}s",
                    serviceName, timeout.TotalSeconds);
                _metrics.RecordTimeout(serviceName);
                return Task.CompletedTask;
            });
    }

    private AsyncTimeoutPolicy<TResult> CreateTimeoutPolicyInternal<TResult>(ResilienceOptions options, string serviceName)
    {
        return Policy.TimeoutAsync<TResult>(
            timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
            timeoutStrategy: TimeoutStrategy.Optimistic,
            onTimeoutAsync: (context, timeout, task) =>
            {
                _logger.LogWarning(
                    "Operation timed out for {Service} after {Timeout}s",
                    serviceName, timeout.TotalSeconds);
                _metrics.RecordTimeout(serviceName);
                return Task.CompletedTask;
            });
    }

    private IAsyncPolicy<TResult> CreateFallbackPolicy<TResult>(
        IFallbackHandler<TResult> fallbackHandler,
        string serviceName)
    {
        return Policy<TResult>
            .Handle<Exception>(ex => fallbackHandler.ShouldFallback(ex))
            .FallbackAsync(
                fallbackAction: async (outcome, context, ct) =>
                {
                    var reason = outcome.Exception?.GetType().Name ?? "Unknown";
                    _logger.LogWarning(outcome.Exception,
                        "Executing fallback for {Service} due to {Reason}",
                        serviceName, reason);
                    _metrics.RecordFallbackExecuted(serviceName, reason);
                    return await fallbackHandler.GetFallbackValueAsync(outcome.Exception!, ct);
                },
                onFallbackAsync: (outcome, context) =>
                {
                    return Task.CompletedTask;
                });
    }

    // ============================================================================
    // HELPERS
    // ============================================================================

    /// <summary>
    /// Calculates delay with exponential backoff and jitter.
    /// Formula: InitialDelayMs * 2^(attempt-1) + random(0, MaxJitterMs)
    /// </summary>
    private static TimeSpan CalculateDelay(ResilienceOptions options, int retryAttempt)
    {
        var exponentialDelay = options.InitialRetryDelayMs * Math.Pow(2, retryAttempt - 1);
        var jitter = options.MaxJitterMs > 0
            ? Random.Shared.NextDouble() * options.MaxJitterMs
            : 0;

        return TimeSpan.FromMilliseconds(exponentialDelay + jitter);
    }

    /// <summary>
    /// Determines if an exception is transient and should be retried.
    /// Covers: HTTP errors, network failures, Stripe, PayPal, Vault, and storage exceptions.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        // HTTP 429 (Too Many Requests) and 503 (Service Unavailable)
        HttpRequestException httpEx when httpEx.StatusCode is
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.BadGateway or
            HttpStatusCode.GatewayTimeout => true,

        // Network-level failures
        HttpRequestException => true,
        TimeoutException => true,
        IOException => true,
        TaskCanceledException tce when tce.InnerException is TimeoutException => true,
        SocketException => true,

        // Connection-related InvalidOperationExceptions
        InvalidOperationException ioe when
            ioe.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) => true,

        // Stripe exceptions (check by type name to avoid hard dependency)
        _ when IsStripeTransient(ex) => true,

        // Vault exceptions (check by type name to avoid hard dependency)
        _ when IsVaultTransient(ex) => true,

        // Minio/S3 exceptions
        _ when IsStorageTransient(ex) => true,

        // PayPal exceptions
        _ when IsPayPalTransient(ex) => true,

        _ => false
    };

    private static bool IsStripeTransient(Exception ex)
    {
        var typeName = ex.GetType().Name;
        if (!string.Equals(typeName, "StripeException", StringComparison.Ordinal)) return false;

        var stripeErrorProp = ex.GetType().GetProperty("StripeError");
        var stripeError = stripeErrorProp?.GetValue(ex);
        if (stripeError == null) return true;

        var codeProp = stripeError.GetType().GetProperty("Code");
        var code = codeProp?.GetValue(stripeError) as string;

        return code is "rate_limit" or "lock_timeout" or "api_connection_error";
    }

    private static bool IsVaultTransient(Exception ex)
    {
        if (!string.Equals(ex.GetType().Name, "VaultApiException", StringComparison.Ordinal)) return false;

        var statusCodeProp = ex.GetType().GetProperty("HttpStatusCode");
        if (statusCodeProp?.GetValue(ex) is HttpStatusCode statusCode)
        {
            return statusCode is
                HttpStatusCode.TooManyRequests or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.BadGateway or
                HttpStatusCode.GatewayTimeout;
        }
        return false;
    }

    private static bool IsStorageTransient(Exception ex)
    {
        var typeName = ex.GetType().Name;

        if (typeName.Contains("Minio", StringComparison.OrdinalIgnoreCase))
        {
            return ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(typeName, "AmazonS3Exception", StringComparison.Ordinal))
        {
            var statusCodeProp = ex.GetType().GetProperty("StatusCode");
            if (statusCodeProp?.GetValue(ex) is HttpStatusCode statusCode)
            {
                return statusCode is
                    HttpStatusCode.TooManyRequests or
                    HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.InternalServerError;
            }
        }

        return false;
    }

    private static bool IsPayPalTransient(Exception ex)
    {
        var typeName = ex.GetType().Name;

        if (typeName.Contains("PayPal", StringComparison.OrdinalIgnoreCase))
        {
            var message = ex.Message;
            return message.Contains("RATE_LIMIT", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("SERVICE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("INTERNAL_SERVER_ERROR", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("connection", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
