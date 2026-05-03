namespace Haworks.BuildingBlocks.Resilience;

/// <summary>
/// Configuration options for bulkhead isolation pattern.
/// Bulkheads limit concurrent execution to prevent resource exhaustion.
/// </summary>
/// <remarks>
/// The bulkhead pattern isolates failures by limiting the resources
/// available to any single operation type, preventing cascade failures.
/// </remarks>
public sealed record BulkheadOptions
{
    /// <summary>
    /// Maximum number of concurrent executions allowed.
    /// Requests beyond this limit are queued (up to MaxQueuingActions).
    /// </summary>
    public int MaxParallelization { get; init; } = 25;

    /// <summary>
    /// Maximum number of actions that can be queued when at capacity.
    /// Requests beyond this limit are rejected immediately.
    /// </summary>
    public int MaxQueuingActions { get; init; } = 50;

    /// <summary>
    /// Default bulkhead settings suitable for most external APIs.
    /// </summary>
    public static BulkheadOptions Default => new();

    /// <summary>
    /// High-throughput settings for services that can handle more concurrent requests.
    /// Use for internal services or high-capacity external APIs.
    /// </summary>
    public static BulkheadOptions HighThroughput => new()
    {
        MaxParallelization = 50,
        MaxQueuingActions = 100
    };

    /// <summary>
    /// Conservative settings for rate-limited or sensitive services.
    /// Use for payment providers, auth services, or services with strict rate limits.
    /// </summary>
    public static BulkheadOptions Conservative => new()
    {
        MaxParallelization = 10,
        MaxQueuingActions = 20
    };

    /// <summary>
    /// Settings optimized for payment providers (Stripe, PayPal).
    /// Balances throughput with provider rate limits.
    /// </summary>
    public static BulkheadOptions PaymentProvider => new()
    {
        MaxParallelization = 25,
        MaxQueuingActions = 50
    };

    /// <summary>
    /// Settings for Vault or secrets management services.
    /// More conservative to protect credential retrieval.
    /// </summary>
    public static BulkheadOptions SecretsManagement => new()
    {
        MaxParallelization = 10,
        MaxQueuingActions = 20
    };

    /// <summary>
    /// Settings for storage services (S3, Minio, Azure Blob).
    /// Higher limits for I/O-bound operations.
    /// </summary>
    public static BulkheadOptions Storage => new()
    {
        MaxParallelization = 50,
        MaxQueuingActions = 100
    };
}
