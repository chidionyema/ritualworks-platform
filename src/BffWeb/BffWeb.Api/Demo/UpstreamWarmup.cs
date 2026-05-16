namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// One-shot warmup BackgroundService — fires GET /health against every
/// upstream microservice once after the BFF starts listening.
///
/// Why this exists: cold-start request storms (catalog stopped, the BFF
/// retries hammered, Polly's circuit breaker tripped open before the
/// backends were ready) used to leave the BFF in a state where every
/// demo endpoint returned 503 with `BrokenCircuitException` until a
/// manual `flyctl machine restart`. The warmup eliminates the very
/// first cold-start race by:
///
///   • Resolving each backend's flycast/internal DNS up front
///   • Opening the first TCP+TLS connection while traffic is zero
///   • Letting Fly's autostart wake any sleeping backends BEFORE
///     visitor traffic hits them
///   • Logging a warmup line per backend so the deploy pipeline
///     surfaces "did the BFF actually reach its dependencies"
///     without waiting for visitor traffic
///
/// Failures here are not fatal — the warmup logs and moves on, and the
/// resilience handler still wraps every call as before. The point is to
/// make the *first* call succeed, not to gate startup on it.
/// </summary>
internal sealed class UpstreamWarmup : BackgroundService
{
    /// <summary>
    /// Backends to warm. Keys must match <see cref="BackendClients"/>
    /// constants exactly; the hosted service resolves typed clients via
    /// the IHttpClientFactory keyed on these names.
    /// </summary>
    private static readonly string[] s_backends =
    {
        BackendClients.Identity,
        BackendClients.Catalog,
        BackendClients.Orders,
        BackendClients.Payments,
        BackendClients.Checkout,
    };

    /// <summary>
    /// Per-backend retry budget. The autostart cold-start can take 10–15s
    /// on Fly's shared-cpu machines; we give each backend up to 6 attempts
    /// at 3s intervals (~18s total per backend).
    /// </summary>
    private const int MaxAttemptsPerBackend = 6;
    private static readonly TimeSpan AttemptInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Wait briefly after Run() so Kestrel has bound :8080 and the host
    /// is fully up. Without this the warmup races our own startup.
    /// </summary>
    private static readonly TimeSpan StartupRamp = TimeSpan.FromSeconds(4);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<UpstreamWarmup> _logger;

    public UpstreamWarmup(IHttpClientFactory httpFactory, ILogger<UpstreamWarmup> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupRamp, stoppingToken); }
        catch (TaskCanceledException) { return; }

        // Warm each backend in parallel — they have no ordering dependency
        // between them. Total wall-clock = max single-backend warmup time.
        var tasks = s_backends.Select(svc => WarmOneAsync(svc, stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task WarmOneAsync(string serviceName, CancellationToken stoppingToken)
    {
        var http = _httpFactory.CreateClient(serviceName);
        // The typed client carries the BaseAddress from service-discovery
        // (https+http://catalog-svc → resolved at request time to
        // http://haworks-catalog.internal:8080). /health is the
        // ASP.NET default health-check path wired by ServiceDefaults
        // (see BuildingBlocks/Extensions/ServiceDefaults.cs:MapDefaultEndpoints).
        for (var attempt = 1; attempt <= MaxAttemptsPerBackend; attempt++)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                using var resp = await http.GetAsync("/health", stoppingToken);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Upstream warmup ok: {Service} attempt {Attempt}/{Max}",
                        serviceName, attempt, MaxAttemptsPerBackend);
                    return;
                }
                _logger.LogDebug(
                    "Upstream warmup non-success: {Service} attempt {Attempt}/{Max} status {Status}",
                    serviceName, attempt, MaxAttemptsPerBackend, (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Upstream warmup attempt {Attempt}/{Max} for {Service} threw — retrying",
                    attempt, MaxAttemptsPerBackend, serviceName);
            }

            try { await Task.Delay(AttemptInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }

        // Out of attempts — log a single warning and stop. Demo controllers
        // continue to wrap their own calls in resilience policies; warmup
        // failure is not fatal.
        _logger.LogWarning(
            "Upstream warmup gave up: {Service} not reachable after {Max} attempts at {Interval}s — first user request may still pay the cold-start cost",
            serviceName, MaxAttemptsPerBackend, AttemptInterval.TotalSeconds);
    }
}
