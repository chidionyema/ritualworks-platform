namespace Haworks.BuildingBlocks.Audit;

/// <summary>
/// Defines a service for logging security-relevant audit events.
/// Implementations should persist events to tamper-proof storage
/// for compliance and security monitoring.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Logs a security audit event asynchronously.
    /// </summary>
    /// <param name="auditEvent">The audit event to log.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogAsync(AuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>
/// Represents a security-relevant audit event.
/// </summary>
public sealed record AuditEvent
{
    /// <summary>
    /// The type of action performed (e.g., "Login", "Logout", "TokenRefresh").
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// The user ID who performed the action. Empty for anonymous actions.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The resource affected by the action (e.g., "User:123", "Order:456").
    /// </summary>
    public required string Resource { get; init; }

    /// <summary>
    /// Whether the action was successful.
    /// </summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// Additional details about the action. Should NOT contain sensitive data.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// IP address of the client, if available.
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>
    /// User agent string, if available.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// When the event occurred. Defaults to UTC now.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Correlation ID for request tracing.
    /// </summary>
    public required string CorrelationId { get; init; }
}

/// <summary>
/// Constants for audit action types.
/// </summary>
public static class AuditActions
{
    public const string Login = "Login";
    public const string LoginFailed = "LoginFailed";
    public const string Logout = "Logout";
    public const string TokenRefresh = "TokenRefresh";
    public const string TokenRefreshFailed = "TokenRefreshFailed";
    public const string Register = "Register";
    public const string RegisterFailed = "RegisterFailed";
    public const string ExternalLogin = "ExternalLogin";
    public const string ExternalLoginLink = "ExternalLoginLink";
    public const string ExternalLoginUnlink = "ExternalLoginUnlink";
    public const string PasswordChange = "PasswordChange";
    public const string PasswordReset = "PasswordReset";
    public const string AccountLockout = "AccountLockout";
    public const string AccountUnlock = "AccountUnlock";
    public const string PermissionDenied = "PermissionDenied";
    public const string SuspiciousActivity = "SuspiciousActivity";
}
