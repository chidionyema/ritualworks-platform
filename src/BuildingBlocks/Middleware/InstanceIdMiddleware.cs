using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Haworks.BuildingBlocks.Middleware;

/// <summary>
/// Stamps every outgoing HTTP response with an <c>X-Instance-Id</c> header so the
/// caller can see which replica handled the request. The id is computed once at
/// startup from one of the following sources, in order of preference:
///
///   1. The <c>service.instance.id</c> attribute embedded in
///      <c>OTEL_RESOURCE_ATTRIBUTES</c> (Aspire injects this on every replica).
///   2. The <c>HOSTNAME</c> environment variable.
///   3. <see cref="Environment.MachineName"/>.
///   4. A short fallback GUID — only hit if the runtime gave us nothing else.
///
/// Combined with Aspire's <c>WithReplicas(N)</c>, this turns "one logical service"
/// into "N visibly-distinct backends" from the consumer's point of view. The
/// portfolio site's <c>RequestReceipt</c> component reads the header and renders
/// it next to the trace id, so the visitor can watch instance ids rotate as
/// requests load-balance across replicas.
///
/// The header is intentionally a plain string. We don't expose the full OTel
/// attribute set; <c>service.instance.id</c> alone is enough to demonstrate
/// load-balancing without leaking deployment topology.
/// </summary>
public static class InstanceIdMiddleware
{
    private static readonly string s_instanceId = ComputeInstanceId();

    /// <summary>
    /// The id stamped onto every response from this process. Computed once at
    /// startup; safe to read concurrently.
    /// </summary>
    public static string InstanceId => s_instanceId;

    private static string ComputeInstanceId()
    {
        // Aspire injects OTEL_RESOURCE_ATTRIBUTES in the form
        //   service.name=catalog-svc,service.instance.id=<guid>,service.namespace=...
        // The instance.id is per-replica; the rest are per-service.
        var otel = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        if (!string.IsNullOrWhiteSpace(otel))
        {
            foreach (var kvp in otel.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = kvp.Split('=', 2);
                if (parts.Length == 2 && string.Equals(parts[0].Trim(), "service.instance.id", StringComparison.Ordinal))
                {
                    var raw = parts[1].Trim();
                    return Shorten(raw);
                }
            }
        }

        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            return Shorten(hostname);
        }

        var machine = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machine))
        {
            return Shorten(machine);
        }

        return Guid.NewGuid().ToString("N")[..8];
    }

    private static string Shorten(string raw)
    {
        // Display-friendly: keep service prefix if present, plus 4-char suffix.
        // e.g. "catalog-svc-replica-2-abcdef0123" → "catalog-svc-2-cdef"
        // For arbitrary GUIDs: "<guid>" → first 8 chars.
        var trimmed = raw.Length > 24 ? raw[..8] + raw[^4..] : raw;
        return trimmed;
    }

    /// <summary>
    /// Wires the middleware. Place after routing, before authentication —
    /// the header should be stamped on every response, including 4xx and 5xx,
    /// so the caller can correlate failures to specific instances.
    /// </summary>
    public static IApplicationBuilder UseInstanceIdHeader(this IApplicationBuilder app)
    {
        app.Use(static async (ctx, next) =>
        {
            ctx.Response.OnStarting(static state =>
            {
                var c = (HttpContext)state;
                if (!c.Response.Headers.ContainsKey("X-Instance-Id"))
                {
                    c.Response.Headers["X-Instance-Id"] = s_instanceId;
                }
                return Task.CompletedTask;
            }, ctx);

            await next();
        });

        return app;
    }
}
