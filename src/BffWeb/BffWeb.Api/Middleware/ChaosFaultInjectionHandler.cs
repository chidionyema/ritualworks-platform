using Haworks.BffWeb.Api.Demo;

namespace Haworks.BffWeb.Api.Middleware;

/// <summary>
/// Thrown by <see cref="ChaosFaultInjectionHandler"/> when the upstream
/// service is currently fault-injected. Distinct from
/// <see cref="HttpRequestException"/> so the StandardResilienceHandler's
/// default retry predicate doesn't classify it as transient and burn
/// 3 seconds retrying a deliberately-injected failure.
/// </summary>
public sealed class ChaosFaultInjectedException : Exception
{
    public string ServiceName { get; }
    public ChaosFaultInjectedException(string serviceName)
        : base($"Chaos: '{serviceName}' is fault-injected (paused via topology map).")
    {
        ServiceName = serviceName;
    }
}

/// <summary>
/// Outbound DelegatingHandler that short-circuits requests to a fault-
/// injected upstream service with a synthetic 503 response. Lets the
/// chaos manager simulate a service being down without touching the
/// upstream process — safe even though it isn't process-level chaos.
///
/// Registered on each named HttpClient with the matching service name
/// (closed over so the handler doesn't need to inspect resolved URIs).
/// Composes <i>before</i> the upstream-instance-id capture handler so a
/// fault-injected request never makes a network call and the captured
/// hop list correctly stays empty.
/// </summary>
internal sealed class ChaosFaultInjectionHandler : DelegatingHandler
{
    private const string FaultMessage =
        "Chaos: upstream service is fault-injected (paused via topology map). Auto-resumes shortly.";

    private readonly ChaosManager? _manager;
    private readonly string _serviceName;

    public ChaosFaultInjectionHandler(ChaosManager? manager, string serviceName)
    {
        _manager = manager;
        _serviceName = serviceName;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Manager is null in non-Development environments — no chaos in prod.
        if (_manager is not null && _manager.IsServiceInjected(_serviceName))
        {
            // Throw rather than return a 503 response: the global
            // StandardResilienceHandler's retry predicate matches on 5xx
            // status codes and would burn 3s of backoff retries on every
            // chaos call. Throwing a non-HttpRequestException bypasses
            // both the retry predicate and the circuit-breaker
            // accounting. The DemoController's existing catch translates
            // exceptions to a 503 response for the browser.
            throw new ChaosFaultInjectedException(_serviceName);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
