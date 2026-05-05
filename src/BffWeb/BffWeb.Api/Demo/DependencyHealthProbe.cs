using System.Diagnostics;
using Haworks.BffWeb.Application.Interfaces;
using MassTransit;

namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// Live dependency probe for BffWeb. Each backing target gets a real round-trip
/// with a short timeout. The result drives <c>/api/health/snapshot</c> so the
/// portfolio's StatusStrip shows the actual state of the seven-service
/// distributed system instead of a static "all online" claim.
///
/// Targets probed:
/// <list type="bullet">
///   <item><c>api</c> — self (online by definition since we are inside the request).</item>
///   <item>five downstream microservices — typed <c>HttpClient</c> GET to
///         <c>/health</c>; status comes from the HTTP response.</item>
///   <item><c>rabbitmq</c> — <c>IBus.Address</c> sanity (MassTransit doesn't expose a sync probe).</item>
/// </list>
///
/// BffWeb has no DbContext/Redis/Vault — the things it depends on are
/// services, so reporting on services is the honest move for a BFF.
/// </summary>
public sealed class DependencyHealthProbe : IDependencyHealthProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly (string Id, string Name, string ClientName)[] s_downstream =
    [
        ("identity",  "IDENTITY",  BackendClients.Identity),
        ("catalog",   "CATALOG",   BackendClients.Catalog),
        ("orders",    "ORDERS",    BackendClients.Orders),
        ("payments",  "PAYMENTS",  BackendClients.Payments),
        ("checkout",  "CHECKOUT",  BackendClients.Checkout),
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<DependencyHealthProbe> _logger;

    public DependencyHealthProbe(
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        ILogger<DependencyHealthProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<DependencySnapshot> ProbeAsync(CancellationToken ct = default)
    {
        var tasks = new List<Task<DependencyStatus>> { ProbeApiAsync() };
        foreach (var (id, name, clientName) in s_downstream)
        {
            tasks.Add(ProbeHttpAsync(id, name, clientName, ct));
        }
        tasks.Add(ProbeRabbitMqAsync());

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Aggregate: any offline -> degraded; all online -> healthy. There's
        // no "down" state at the BFF level since the BFF itself answering
        // means "api" is online — true outage means we couldn't respond at all.
        var aggregate = results.All(r => r.Status == "online") ? "healthy" : "degraded";

        return new DependencySnapshot(results, aggregate, DateTime.UtcNow);
    }

    private static Task<DependencyStatus> ProbeApiAsync() =>
        Task.FromResult(new DependencyStatus("api", "BFF-WEB", "online", 0, null));

    private async Task<DependencyStatus> ProbeHttpAsync(string id, string name, string clientName, CancellationToken ct) =>
        await TimedAsync(id, name, async () =>
        {
            var client = _httpClientFactory.CreateClient(clientName);
            // Aspire service-discovery resolves https+http://<svc> to whichever
            // endpoint the target advertises. /health is provided by Aspire's
            // ServiceDefaults.MapDefaultEndpoints — every platform service has it.
            using var resp = await client.GetAsync("/health", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return ("online", (string?)null);
            }
            return ("degraded", $"HTTP {(int)resp.StatusCode}");
        }).ConfigureAwait(false);

    private async Task<DependencyStatus> ProbeRabbitMqAsync() =>
        await TimedAsync("mq", "RABBITMQ", async () =>
        {
            var bus = _services.GetService<IBus>();
            if (bus is null) return ("offline", "IBus not registered");
            if (bus.Address is null) return ("offline", "bus has no remote address");

            await Task.CompletedTask;
            return ("online", (string?)null);
        }).ConfigureAwait(false);

    private async Task<DependencyStatus> TimedAsync(
        string id,
        string name,
        Func<Task<(string Status, string? Message)>> probe)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(ProbeTimeout);
            var probeTask = probe();
            var winner = await Task.WhenAny(probeTask, Task.Delay(ProbeTimeout, cts.Token)).ConfigureAwait(false);
            sw.Stop();

            if (winner != probeTask)
            {
                return new DependencyStatus(id, name, "offline", sw.ElapsedMilliseconds, "probe timed out");
            }

            var (status, message) = await probeTask.ConfigureAwait(false);
            return new DependencyStatus(id, name, status, sw.ElapsedMilliseconds, message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Dependency probe failed for {Id}", id);
            return new DependencyStatus(id, name, "offline", sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }
}
