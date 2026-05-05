using System.Diagnostics;
using Haworks.BffWeb.Application.Interfaces;

namespace Haworks.BffWeb.Api.Middleware;

/// <summary>
/// Records every <c>/api/demo/*</c> request into <see cref="IDemoActivityCounters"/>
/// so the hero metrics tile reflects this BffWeb instance's actual activity
/// instead of hardcoded numbers. Reads <c>X-Demo-Session</c> for the active-
/// sessions count and stamps total request duration into the rolling P99
/// histogram. Wired before <c>UseAuthentication</c> so a 401 still records
/// the traffic.
/// </summary>
public static class DemoActivityMiddleware
{
    public static IApplicationBuilder UseDemoActivityCounters(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isDemoRoute = path.StartsWithSegments("/api/demo", StringComparison.OrdinalIgnoreCase);

            if (!isDemoRoute)
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
                var counters = context.RequestServices.GetService<IDemoActivityCounters>();
                if (counters is not null)
                {
                    var sessionId = context.Request.Headers.TryGetValue("X-Demo-Session", out var v)
                        ? v.ToString()
                        : null;
                    counters.RecordRequest(sessionId, sw.Elapsed.TotalMilliseconds);
                }
            }
        });
    }
}
