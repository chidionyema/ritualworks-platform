using System.Net.Http.Json;
using Haworks.BffWeb.Api.SignalR;
using Microsoft.AspNetCore.SignalR;

namespace Haworks.BffWeb.Api.Demo;

/// <summary>
/// JourneyScheduler — server-side BackgroundService that fires a rotating
/// canonical journey through the cluster every <see cref="JourneyIntervalSeconds"/>
/// seconds. Three journeys, each exercising different patterns:
///
///   • <c>place-order-saga</c> — POST /api/demo/saga/start, polls until terminal
///     (Completed | Abandoned). Hits checkout → catalog → payments via MassTransit.
///   • <c>idempotent-retry</c> — POSTs the same X-Idempotency-Key 5× to
///     /api/demo/idempotency/process. Orders' ON CONFLICT returns the same
///     claim id every time after the first.
///   • <c>occ-race</c> — Fires N concurrent PUTs to /api/demo/inventory/{id}
///     all carrying the same xmin in If-Match. Postgres lets exactly one win;
///     the rest get 412.
///
/// The scheduler pushes <c>OnJourneyStart</c> + <c>OnJourneyEnd</c> events on
/// the LiveConsoleHub so the frontend can lock onto the active journey and
/// choreograph its topology animation accordingly. The actual per-request
/// events still flow through the existing LiveConsoleMiddleware → OnConsoleEvent
/// pipeline; the journey events are the wrapper that lets the frontend group
/// them.
///
/// Why server-side: the cluster looks alive even with zero visitors. Same
/// reasoning as the existing LabBackgroundProber on the frontend, but fired
/// from inside the BFF so it runs in the production deploy regardless of
/// page loads.
/// </summary>
public sealed class JourneyScheduler : BackgroundService
{
    private const int JourneyIntervalSeconds = 20;
    private const int InitialDelaySeconds = 8;

    private static readonly string[] s_journeys =
    {
        "place-order-saga",
        "idempotent-retry",
        "occ-race",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IHubContext<LiveConsoleHub> _hub;
    private readonly ILogger<JourneyScheduler> _logger;
    private readonly IConfiguration _config;
    private int _index;

    public JourneyScheduler(
        IHttpClientFactory httpFactory,
        IHubContext<LiveConsoleHub> hub,
        IConfiguration config,
        ILogger<JourneyScheduler> logger)
    {
        _httpFactory = httpFactory;
        _hub = hub;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief ramp-up so the BFF, downstream services, and SignalR hub all
        // have time to settle before we start firing.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(InitialDelaySeconds), stoppingToken);
        }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var journey = s_journeys[_index % s_journeys.Length];
            _index++;

            var sessionId = Guid.NewGuid();
            var started = DateTimeOffset.UtcNow;
            await BroadcastStartAsync(journey, sessionId, started, stoppingToken);

            var ok = false;
            try
            {
                ok = journey switch
                {
                    "place-order-saga" => await RunPlaceOrderSagaAsync(stoppingToken),
                    "idempotent-retry" => await RunIdempotentRetryAsync(stoppingToken),
                    "occ-race"         => await RunOccRaceAsync(stoppingToken),
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Journey {Journey} threw — continuing rotation", journey);
            }

            await BroadcastEndAsync(journey, sessionId, started, ok, stoppingToken);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(JourneyIntervalSeconds), stoppingToken);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    // ───────────────────────────── Journey 1 ─────────────────────────────

    private async Task<bool> RunPlaceOrderSagaAsync(CancellationToken ct)
    {
        var http = LoopbackClient();

        // Kick the saga.
        using var startResp = await http.PostAsJsonAsync(
            "/api/demo/saga/start",
            new { scenarioType = "success", simulatedDelayMs = 200 },
            ct);
        if (!startResp.IsSuccessStatusCode) return false;

        var startBody = await startResp.Content.ReadFromJsonAsync<SagaStartResponse>(ct);
        if (startBody?.SessionId is not Guid sagaId) return false;

        // Poll to terminal state. 12 polls × 1s = 12s ceiling.
        var terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Completed", "Abandoned", "Failed" };
        for (var i = 0; i < 12; i++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (TaskCanceledException) { return false; }

            using var pollResp = await http.GetAsync($"/api/demo/saga/{sagaId}", ct);
            if (!pollResp.IsSuccessStatusCode) continue;
            var poll = await pollResp.Content.ReadFromJsonAsync<SagaStatusResponse>(ct);
            if (poll?.Status is { } s && terminal.Contains(s))
            {
                return true;
            }
        }
        return false;
    }

    // ───────────────────────────── Journey 2 ─────────────────────────────

    private async Task<bool> RunIdempotentRetryAsync(CancellationToken ct)
    {
        var http = LoopbackClient();
        var key = $"journey-idemp-{Guid.NewGuid():N}";

        for (var i = 0; i < 5; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/demo/idempotency/process")
            {
                Content = JsonContent.Create(new { amount = 99.99 }),
            };
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", key);
            req.Headers.TryAddWithoutValidation("X-Idempotency-Ttl-Seconds", "300");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode && i == 0)
            {
                // First write needs to succeed for the rest of the journey to mean anything.
                return false;
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(400), ct); }
            catch (TaskCanceledException) { return false; }
        }
        return true;
    }

    // ───────────────────────────── Journey 3 ─────────────────────────────

    private async Task<bool> RunOccRaceAsync(CancellationToken ct)
    {
        var http = LoopbackClient();

        // Resolve the demo product id + current xmin from catalog.
        using var seedResp = await http.GetAsync("/api/demo/cache/product/demo", ct);
        if (!seedResp.IsSuccessStatusCode) return false;
        var seed = await seedResp.Content.ReadFromJsonAsync<DemoProductResponse>(ct);
        if (seed?.Id is not Guid productId) return false;

        using var invResp = await http.GetAsync($"/api/demo/inventory/{productId}", ct);
        if (!invResp.IsSuccessStatusCode) return false;
        var inv = await invResp.Content.ReadFromJsonAsync<InventoryResponse>(ct);
        var xmin = inv?.Xmin?.ToString() ?? inv?.Version?.ToString();
        if (string.IsNullOrEmpty(xmin)) return false;

        // Fire 5 concurrent PUTs all carrying the same xmin in If-Match.
        var stock = inv?.Stock ?? inv?.Quantity ?? 1000;
        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/demo/inventory/{productId}")
            {
                Content = JsonContent.Create(new { stock }),
            };
            put.Headers.TryAddWithoutValidation("If-Match", xmin);
            using var r = await http.SendAsync(put, ct);
            return r.IsSuccessStatusCode;
        });
        var results = await Task.WhenAll(tasks);
        var winners = results.Count(ok => ok);
        return winners == 1; // Invariant: exactly one winner per OCC race batch.
    }

    // ─────────────────────────── Helpers ───────────────────────────

    private HttpClient LoopbackClient()
    {
        var http = _httpFactory.CreateClient(nameof(JourneyScheduler));
        // Resolve the BFF's own bound URL. Aspire injects this via
        // ASPNETCORE_URLS; in production, fall back to the canonical Fly host.
        var baseUrl =
            _config["JourneyScheduler:LoopbackUrl"]
            ?? FirstUrl(_config["ASPNETCORE_URLS"])
            ?? _config["JourneyScheduler:FallbackUrl"]
            ?? throw new InvalidOperationException(
                "JourneyScheduler loopback URL not configured. Set JourneyScheduler:LoopbackUrl, ASPNETCORE_URLS, or JourneyScheduler:FallbackUrl.");
        http.BaseAddress = new Uri(baseUrl);
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }

    private static string? FirstUrl(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var first = csv.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first?.Trim();
    }

    private async Task BroadcastStartAsync(
        string journey,
        Guid sessionId,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "OnJourneyStart",
                new
                {
                    journey,
                    sessionId,
                    startedAt = startedAt.ToString("O"),
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OnJourneyStart broadcast failed (non-fatal)");
        }
    }

    private async Task BroadcastEndAsync(
        string journey,
        Guid sessionId,
        DateTimeOffset startedAt,
        bool ok,
        CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "OnJourneyEnd",
                new
                {
                    journey,
                    sessionId,
                    startedAt = startedAt.ToString("O"),
                    endedAt = DateTimeOffset.UtcNow.ToString("O"),
                    ok,
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OnJourneyEnd broadcast failed (non-fatal)");
        }
    }

    private sealed record SagaStartResponse(Guid? SessionId);
    private sealed record SagaStatusResponse(string? Status);
    private sealed record DemoProductResponse(Guid? Id);
    private sealed record InventoryResponse(int? Xmin, int? Version, int? Stock, int? Quantity);
}
