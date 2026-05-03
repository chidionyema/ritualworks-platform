using Microsoft.Extensions.Logging;

namespace Haworks.BuildingBlocks.Audit;

/// <summary>
/// Structured audit logger that writes security events to application logs.
///
/// For production use, consider:
/// - Using a dedicated audit log sink (separate from application logs)
/// - Writing to append-only storage (Azure Blob, S3, database table)
/// - Adding tamper-evident mechanisms (signatures, blockchain)
/// - Implementing log forwarding to SIEM systems
/// </summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(ILogger<AuditLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task LogAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Use warning level for failed security actions
        if (!auditEvent.IsSuccess || IsSecuritySensitive(auditEvent.Action))
        {
            _logger.LogWarning(
                "[SECURITY] Action={Action}, UserId={UserId}, Resource={Resource}, Success={IsSuccess}, Details={Details}, IP={IpAddress}, UserAgent={UserAgent}, CorrelationId={CorrelationId}, Timestamp={Timestamp}",
                auditEvent.Action,
                auditEvent.UserId,
                auditEvent.Resource,
                auditEvent.IsSuccess,
                auditEvent.Details,
                MaskIpAddress(auditEvent.IpAddress),
                TruncateUserAgent(auditEvent.UserAgent),
                auditEvent.CorrelationId,
                auditEvent.Timestamp);
        }
        else
        {
            _logger.LogInformation(
                "[AUDIT] Action={Action}, UserId={UserId}, Resource={Resource}, Success={IsSuccess}, Details={Details}, IP={IpAddress}, UserAgent={UserAgent}, CorrelationId={CorrelationId}, Timestamp={Timestamp}",
                auditEvent.Action,
                auditEvent.UserId,
                auditEvent.Resource,
                auditEvent.IsSuccess,
                auditEvent.Details,
                MaskIpAddress(auditEvent.IpAddress),
                TruncateUserAgent(auditEvent.UserAgent),
                auditEvent.CorrelationId,
                auditEvent.Timestamp);
        }

        // For now, we're logging synchronously. In production, consider:
        // - Async writes to a message queue
        // - Batch writes to a database
        // - Fire-and-forget with retry mechanisms
        return Task.CompletedTask;
    }

    /// <summary>
    /// Determines if an action is security-sensitive and should be logged at warning level.
    /// </summary>
    private static bool IsSecuritySensitive(string action) => action switch
    {
        AuditActions.LoginFailed => true,
        AuditActions.TokenRefreshFailed => true,
        AuditActions.RegisterFailed => true,
        AuditActions.AccountLockout => true,
        AuditActions.PermissionDenied => true,
        AuditActions.SuspiciousActivity => true,
        _ => false
    };

    /// <summary>
    /// Masks the last octet of IPv4 addresses for privacy.
    /// Example: 192.168.1.100 -> 192.168.1.xxx
    /// </summary>
    private static string? MaskIpAddress(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return null;
        }

        // For IPv4, mask last octet
        var lastDot = ipAddress.LastIndexOf('.');
        if (lastDot > 0)
        {
            return ipAddress[..lastDot] + ".xxx";
        }

        // For IPv6 or other formats, truncate
        return ipAddress.Length > 20 ? ipAddress[..20] + "..." : ipAddress;
    }

    /// <summary>
    /// Truncates user agent strings to prevent log bloat.
    /// </summary>
    private static string? TruncateUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return null;
        }

        const int maxLength = 100;
        return userAgent.Length > maxLength ? userAgent[..maxLength] + "..." : userAgent;
    }
}
