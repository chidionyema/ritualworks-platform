using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Haworks.BuildingBlocks.Middleware;

/// <summary>
/// <see cref="DelegatingHandler"/> that reads the correlation id from the
/// current <see cref="HttpContext"/> (set by <see cref="CorrelationIdMiddleware"/>)
/// and stamps it onto every outbound HTTP request as <c>X-Correlation-ID</c>,
/// so downstream services see the same id and can log it.
///
/// Also tags the current OTel <see cref="Activity"/> with
/// <c>correlation.id</c> — Tempo / Grafana can then filter spans by the same
/// id support is grepping for in Loki, giving a single key that joins logs
/// and traces across the BFF→backend hop.
/// </summary>
public sealed class CorrelationIdHttpClientHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    /// <summary>The OTel span tag key. Dotted form, per OTel semantic conventions.</summary>
    public const string ActivityTagName = "correlation.id";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var ctx = accessor.HttpContext;
        if (ctx is not null
            && ctx.Items.TryGetValue(CorrelationIdMiddleware.ItemsKey, out var raw)
            && raw is string id
            && !string.IsNullOrWhiteSpace(id))
        {
            // Don't overwrite an explicit caller-set header — preserves
            // unusual flows (e.g. a worker forwarding an upstream id) and
            // keeps this handler idempotent.
            if (!request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
            {
                request.Headers.Add(CorrelationIdMiddleware.HeaderName, id);
            }

            // SetTag (not AddTag) because we want at most one correlation id
            // per span. Activity.Current may be null outside an OTel scope —
            // SetTag on a null activity is a no-op via ?., which is fine.
            Activity.Current?.SetTag(ActivityTagName, id);
        }

        return base.SendAsync(request, ct);
    }
}
