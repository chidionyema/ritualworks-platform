namespace Haworks.BuildingBlocks.Resilience.Constants;

/// <summary>
/// Centralized constants for resilience policies (retry, circuit breaker, timeouts).
/// </summary>
public static class ResilienceConstants
{
    /// <summary>
    /// Default resilience settings.
    /// </summary>
    public static class Default
    {
        public const int MaxRetryAttempts = 3;
        public const int InitialRetryDelayMs = 200;
        public const int MaxJitterMs = 100;
        public const int CircuitBreakerThreshold = 5;
        public const int CircuitBreakerDurationSeconds = 30;
    }

    /// <summary>
    /// Stripe-specific resilience settings.
    /// </summary>
    public static class Stripe
    {
        public const int MaxRetryAttempts = 3;
        public const int InitialRetryDelayMs = 1000;
        public const int MaxJitterMs = 100;
        public const int CircuitBreakerThreshold = 5;
        public const int CircuitBreakerDurationSeconds = 30;
    }

    /// <summary>
    /// PayPal-specific resilience settings.
    /// </summary>
    public static class PayPal
    {
        public const int MaxRetryAttempts = 3;
        public const int InitialRetryDelayMs = 500;
        public const int MaxJitterMs = 200;
        public const int CircuitBreakerThreshold = 5;
        public const int CircuitBreakerDurationSeconds = 60;
    }

    /// <summary>
    /// Vault-specific resilience settings.
    /// </summary>
    public static class Vault
    {
        public const int MaxRetryAttempts = 5;
        public const int InitialRetryDelayMs = 200;
        public const int MaxJitterMs = 50;
        public const int CircuitBreakerThreshold = 5;
        public const int CircuitBreakerDurationSeconds = 30;
    }

    /// <summary>
    /// Storage-specific resilience settings.
    /// </summary>
    public static class Storage
    {
        public const int MaxRetryAttempts = 3;
        public const int InitialRetryDelayMs = 200;
        public const int MaxJitterMs = 50;
        public const int CircuitBreakerThreshold = 5;
        public const int CircuitBreakerDurationSeconds = 30;
    }

    /// <summary>
    /// Database-specific resilience settings.
    /// </summary>
    public static class Database
    {
        public const int MaxRetryAttempts = 5;
        public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Stock management resilience settings.
    /// </summary>
    public static class Stock
    {
        /// <summary>Timeout for stock release operations during compensation.</summary>
        public static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// HTTP client settings.
    /// </summary>
    public static class HttpClient
    {
        public const int DefaultTimeoutSeconds = 60;
        public const int MaxPoolSize = 50;
    }

    /// <summary>
    /// MassTransit message retry settings.
    /// </summary>
    public static class MassTransit
    {
        /// <summary>
        /// Default timeout for message handlers.
        /// Messages taking longer than this are considered hung and will fail.
        /// </summary>
        public static readonly TimeSpan DefaultMessageTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Timeout for payment-related message handlers.
        /// Longer timeout to account for external API calls.
        /// </summary>
        public static readonly TimeSpan PaymentMessageTimeout = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Timeout for quick message handlers (notifications, logging).
        /// </summary>
        public static readonly TimeSpan QuickMessageTimeout = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Retry intervals for RabbitMQ transport (production/microservices).
        /// Longer intervals with additional retry for network resilience.
        /// </summary>
        public static readonly TimeSpan[] RabbitMqRetryIntervals =
        [
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30)
        ];
    }

    /// <summary>
    /// Bulkhead isolation settings for resource limiting.
    /// </summary>
    public static class Bulkhead
    {
        /// <summary>Default bulkhead settings for most services.</summary>
        public static class Default
        {
            public const int MaxParallelization = 25;
            public const int MaxQueuingActions = 50;
        }

        /// <summary>Payment provider bulkhead (Stripe, PayPal).</summary>
        public static class PaymentProvider
        {
            public const int MaxParallelization = 25;
            public const int MaxQueuingActions = 50;
        }

        /// <summary>Secrets management bulkhead (Vault).</summary>
        public static class SecretsManagement
        {
            public const int MaxParallelization = 10;
            public const int MaxQueuingActions = 20;
        }

        /// <summary>Storage bulkhead (S3, Minio).</summary>
        public static class Storage
        {
            public const int MaxParallelization = 50;
            public const int MaxQueuingActions = 100;
        }

        /// <summary>High-throughput bulkhead for internal services.</summary>
        public static class HighThroughput
        {
            public const int MaxParallelization = 50;
            public const int MaxQueuingActions = 100;
        }

        /// <summary>Conservative bulkhead for rate-limited services.</summary>
        public static class Conservative
        {
            public const int MaxParallelization = 10;
            public const int MaxQueuingActions = 20;
        }
    }
}
