using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace Haworks.BuildingBlocks.Middleware;

/// <summary>
/// Reads (or mints) the <c>X-Correlation-ID</c> header on every inbound request,
/// stamps it onto the response, stashes it in <see cref="HttpContext.Items"/>
/// under the <see cref="ItemsKey"/> key, and pushes it into Serilog
/// <see cref="LogContext"/> as <c>CorrelationId</c> so every log line emitted
/// inside the request scope carries the same id.
///
/// This is complementary to OTel <c>traceparent</c> propagation — traceparent
/// is the machine-readable span chain; correlation-id is the human-readable
/// handle that support / on-call greps for in Loki when they only have a
/// single id from the customer.
/// </summary>
public static class CorrelationIdMiddleware
{
    /// <summary>The wire header name. Same casing as the de-facto standard.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>
    /// Key under which the resolved id is stashed in <see cref="HttpContext.Items"/>.
    /// The outbound <c>CorrelationIdHttpClientHandler</c> reads from this.
    /// </summary>
    public const string ItemsKey = "CorrelationId";

    /// <summary>
    /// Registers the correlation-id middleware. Should run as early as possible
    /// — before routing, auth, anything that might log — so every log line in
    /// the request lifecycle carries the id.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        app.Use(static async (ctx, next) =>
        {
            // Resolve the id, in priority order:
            //   1. Already stashed in HttpContext.Items — re-entry case;
            //      treat the stashed value as authoritative so we don't
            //      double-push or mint a new id.
            //   2. Inbound X-Correlation-ID header — caller / upstream
            //      service supplied an id we should preserve end-to-end.
            //   3. Mint a fresh id. No ULID lib referenced in BuildingBlocks
            //      — Guid("N") is a lib-free, log-friendly fallback (32 hex,
            //      no hyphens). Sortability isn't a requirement here.
            string id;
            var alreadyStashed = ctx.Items.TryGetValue(ItemsKey, out var existing)
                && existing is string preset && !string.IsNullOrWhiteSpace(preset);

            if (alreadyStashed)
            {
                id = (string)existing!;
            }
            else if (ctx.Request.Headers.TryGetValue(HeaderName, out var headerValues)
                && !string.IsNullOrWhiteSpace(headerValues.ToString()))
            {
                id = headerValues.ToString();
                ctx.Items[ItemsKey] = id;
            }
            else
            {
                id = Guid.NewGuid().ToString("N");
                ctx.Items[ItemsKey] = id;
            }

            // Stamp the id back on the response so the caller can read it.
            // OnStarting because headers are immutable once the response has
            // begun streaming, and downstream middleware may set status codes
            // before we get here. ContainsKey guards re-entry.
            ctx.Response.OnStarting(static state =>
            {
                var c = (HttpContext)state;
                if (c.Items.TryGetValue(ItemsKey, out var v) && v is string s
                    && !c.Response.Headers.ContainsKey(HeaderName))
                {
                    c.Response.Headers[HeaderName] = s;
                }
                return Task.CompletedTask;
            }, ctx);

            using (LogContext.PushProperty(ItemsKey, id))
            {
                await next();
            }
        });

        return app;
    }
}
