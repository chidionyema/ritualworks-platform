using Microsoft.AspNetCore.Http;

namespace Haworks.BffWeb.Api.Middleware;

/// <summary>
/// Per-call <see cref="DelegatingHandler"/> that records the
/// <c>X-Instance-Id</c> response header of every upstream service call into
/// <see cref="HttpContext.Items"/>. The <see cref="LiveConsoleMiddleware"/>
/// reads the captured list at the end of the request and stamps the hops
/// onto the emitted <see cref="Demo.LiveConsoleEvent"/> so the dock can show
/// the actual replica that handled each upstream — e.g.
/// <c>bff-yumv → catalog-svc-7e3f</c>.
///
/// The handler is registered per named-client; the constructor takes the
/// short logical service name (<c>catalog</c>, <c>orders</c>, etc.) so each
/// hop knows which service it belongs to without inspecting the resolved
/// URI (which after Aspire's service-discovery rewrite is a hostname:port,
/// not a friendly name).
/// </summary>
internal sealed class UpstreamInstanceCaptureHandler : DelegatingHandler
{
    /// <summary>Key used in <see cref="HttpContext.Items"/> for the hop list.</summary>
    public const string ItemsKey = "live-console.upstreams";

    private readonly IHttpContextAccessor _accessor;
    private readonly ILogger<UpstreamInstanceCaptureHandler> _logger;
    private readonly string _service;

    public UpstreamInstanceCaptureHandler(IHttpContextAccessor accessor, ILogger<UpstreamInstanceCaptureHandler> logger, string service)
    {
        _accessor = accessor;
        _logger = logger;
        _service = service;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            // Capture even on non-2xx — a 503 from a specific replica is
            // valuable signal for the dock. Wrapped in try because nothing
            // upstream of the handler should fail just because we couldn't
            // record a hop.
            try
            {
                var ctx = _accessor.HttpContext;
                if (ctx is not null && response is not null
                    && response.Headers.TryGetValues("X-Instance-Id", out var ids))
                {
                    var instance = ids.FirstOrDefault();
                    if (!string.IsNullOrEmpty(instance))
                    {
                        if (ctx.Items[ItemsKey] is not List<UpstreamHop> hops)
                        {
                            hops = new List<UpstreamHop>(4);
                            ctx.Items[ItemsKey] = hops;
                        }
                        hops.Add(new UpstreamHop(_service, instance));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "An error occurred in {MethodName}", nameof(SendAsync));
            }
        }
    }
}

/// <summary>
/// One hop recorded from an outbound HttpClient call. Service is the short
/// logical name (matches the AppHost resource id). InstanceId is the value
/// of the upstream response's <c>X-Instance-Id</c> header — the replica
/// that actually handled the call.
/// </summary>
public sealed record UpstreamHop(string Service, string InstanceId);
