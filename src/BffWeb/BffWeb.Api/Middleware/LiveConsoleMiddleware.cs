using System.Diagnostics;
using Haworks.BffWeb.Api.Demo;
using Haworks.BuildingBlocks.Middleware;

namespace Haworks.BffWeb.Api.Middleware;

/// <summary>
/// Captures every <c>/api/*</c> request as a <see cref="LiveConsoleEvent"/>
/// and forwards it to the <see cref="LiveConsoleBroadcaster"/> so the
/// visitor-facing dock can render real-time activity.
///
/// Path-scoped to <c>/api/*</c> so static files, hub negotiations, and
/// health probes don't pollute the dock. Wired after the demo activity
/// counters so we don't double-instrument latency, and after CORS so we
/// only emit for requests the browser was actually allowed to send.
/// </summary>
public static class LiveConsoleMiddleware
{
    public static IApplicationBuilder UseLiveConsole(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isApi = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

            if (!isApi)
            {
                await next();
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await next();
            }
            finally
            {
                sw.Stop();

                var broadcaster = context.RequestServices.GetService<LiveConsoleBroadcaster>();
                if (broadcaster is not null)
                {
                    // Capture upstream hops recorded by UpstreamInstanceCaptureHandler.
                    // Empty when this request didn't fan out (e.g. a /health probe).
                    var hops = context.Items[UpstreamInstanceCaptureHandler.ItemsKey]
                                   as IReadOnlyList<UpstreamHop>
                               ?? Array.Empty<UpstreamHop>();

                    var ev = new LiveConsoleEvent
                    {
                        Ts = DateTime.UtcNow.ToString("o"),
                        Service = "bff-web",
                        InstanceId = InstanceIdMiddleware.InstanceId,
                        Method = context.Request.Method,
                        Path = context.Request.Path.Value ?? "/",
                        Status = context.Response.StatusCode,
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        TraceId = Activity.Current?.TraceId.ToString(),
                        CorrelationId = context.Request.Headers.TryGetValue("X-Correlation-ID", out var cid)
                            ? cid.ToString()
                            : null,
                        Upstreams = hops
                    };

                    broadcaster.Emit(ev);
                }
            }
        });
    }
}
