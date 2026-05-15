using System.Net;
using Microsoft.AspNetCore.Http;

namespace Haworks.BuildingBlocks.Audit;

/// <summary>
/// Extension methods for HttpContext to provide consistent access to
/// correlation IDs, client IP addresses, and other request metadata.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Gets the correlation ID for the current request.
    /// Checks CorrelationId in Items, then TraceIdentifier, then generates a new GUID.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A correlation ID string.</returns>
    public static string GetCorrelationId(this HttpContext context)
    {
        return context.Items["CorrelationId"]?.ToString()
            ?? context.TraceIdentifier
            ?? Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Gets the client's IP address, accounting for proxy/load balancer headers.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The client IP address, or null if not determinable.</returns>
    /// <remarks>
    /// Checks X-Forwarded-For header first (common behind proxies/load balancers),
    /// then falls back to RemoteIpAddress.
    /// </remarks>
    public static string? GetClientIpAddress(this HttpContext context)
    {
        // Check for X-Forwarded-For header (common behind proxies/load balancers)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // Take the first IP in the list (original client) and validate format
            var candidate = forwardedFor.Split(',').FirstOrDefault()?.Trim();
            if (candidate is not null && IPAddress.TryParse(candidate, out _))
            {
                return candidate;
            }
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    /// Gets the User-Agent header value.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The User-Agent string, or empty if not present.</returns>
    public static string GetUserAgent(this HttpContext context)
    {
        return context.Request.Headers.UserAgent.ToString();
    }
}
