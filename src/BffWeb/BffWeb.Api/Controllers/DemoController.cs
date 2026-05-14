using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Haworks.BffWeb.Api;
using Haworks.BffWeb.Api.Demo;
using Haworks.BffWeb.Application.Interfaces;
using Haworks.Contracts.Checkout;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Polly;
using Polly.CircuitBreaker;

namespace Haworks.BffWeb.Api.Controllers;

/// <summary>
/// Demo surface for the portfolio site (https://github.com/chidionyema/portfolio-site).
/// Mirrors the wire contract the frontend expects (src/lib/api/demo-client.ts +
/// signalr.ts). All endpoints are [AllowAnonymous] — the demos are public.
///
/// Phase 1 (this file): three demos run real in-process patterns
/// (idempotency, optimistic concurrency, rate limit), trace/start emits a
/// structurally honest synthetic trace, and the rest return contract-correct
/// stubs marked with `// PHASE 2:` notes.
///
/// Phase 2 (planned): each `// PHASE 2:` stub gets replaced with a real call
/// into the matching microservice — saga -> CheckoutOrchestrator, events ->
/// payments outbox, circuit -> typed HttpClient to catalog with Polly,
/// vault -> identity-svc Vault rotation, cache -> catalog IProductCache,
/// chaos -> shared chaos flag in Vault that downstream services consult.
/// </summary>
[ApiController]
[Route("api/demo")]
public class DemoController : ControllerBase
{
    private readonly IDemoHubNotifier _notifier;
    private readonly DemoStateStore _stateStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DemoController> _logger;

    public DemoController(
        IDemoHubNotifier notifier,
        DemoStateStore stateStore,
        IHttpClientFactory httpClientFactory,
        ILogger<DemoController> logger)
    {
        _notifier = notifier;
        _stateStore = stateStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // ========================================================================
    // Distributed Tracing demo removed. The previous /api/demo/tracing/start
    // synthesised a hardcoded 7-span flame graph; durations and tree shape
    // were baked into the controller, not real OTel data. Real Tempo +
    // cross-service span propagation will land separately before the demo
    // returns.
    // ========================================================================

    // ========================================================================
    // Saga (Checkout Flow) — T2.2: real CheckoutOrchestrator round-trip
    // ========================================================================
    //
    // BffWeb routes /api/demo/saga/start through to checkout-orchestrator-svc
    // (POST /api/checkouts) via the typed HttpClient registered for
    // BackendClients.Checkout. The saga's state machine handles the rest:
    // publishes StockReservationRequestedEvent -> catalog reserves stock ->
    // PaymentSessionRequestedEvent -> payments creates Stripe session -> ...
    //
    // What's still stub: SignalR push of saga state changes
    // (NotifySagaStepAsync). The portfolio-site polls /api/demo/saga/{id}
    // for status as a fallback, so the demo works without push. Real push
    // is T2.2 commit 2 — adds MT consumers in BffWeb that listen for the
    // saga's published events (StockReservationRequested, etc.) and
    // translate each to OnSagaStep.
    //
    // Scenario coverage: only "success" + "stockFailure" work end-to-end
    // today. "paymentFailure" + "stockRace" need catalog/payments to honor
    // a DemoScenario MT header — separate task tracked in
    // docs/agent-briefs/portfolio-bffweb-phase2.md.

    [HttpPost("saga/start")]
    [AllowAnonymous]
    public async Task<IActionResult> StartSaga([FromBody] SagaStartRequest request, CancellationToken ct)
    {
        var sagaId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var idempotencyKey = $"demo-{sagaId:N}-{request.ScenarioType}";

        _logger.LogInformation(
            "Saga demo: routing to checkout-orchestrator scenario={Scenario} sagaId={SagaId}",
            request.ScenarioType, sagaId);

        // Resolve the real demo product id from catalog. The previous
        // hardcoded GUID never existed in the DB, which made the saga's
        // stock consumer always emit StockReservationFailed. Calling
        // /demo/cache/seed-demo-product is idempotent — creates the product
        // with stock 1000 the first time, returns the existing id afterwards.
        Guid demoProductId;
        try
        {
            var seedClient = _httpClientFactory.CreateClient(BackendClients.Catalog);
            using var seedResp = await seedClient.PostAsync("/demo/cache/seed-demo-product", content: null, ct);
            seedResp.EnsureSuccessStatusCode();
            var seedBody = await seedResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            demoProductId = seedBody.GetProperty("productId").GetGuid();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saga demo: catalog seed unreachable; saga will likely fail at stock");
            return StatusCode(503, new { sessionId = sagaId, status = "CatalogUnreachable" });
        }

        var demoItems = new[]
        {
            new CheckoutItemData
            {
                ProductId = demoProductId,
                ProductName = "Demo Widget",
                Quantity = request.ScenarioType == "stockRace" ? 3 : 1,
                UnitPrice = 39.99m,
            },
        };

        if (request.ScenarioType == "stockRace")
        {
            // Two carts, one product, real saga concurrency. Both POSTs go
            // to the orchestrator in parallel; the catalog stock reservation
            // consumer's atomic UPDATE WHERE stock >= qty picks the winner.
            var sagaB = Guid.NewGuid();
            var (resA, resB) = await StartCartsRaceAsync(sagaId, sagaB, orderId, demoItems, idempotencyKey, ct);

            return Accepted(new
            {
                status = "RaceStarted",
                sessionId = sagaId,
                races = new[]
                {
                    new { sagaId, orderId, label = "Cart_A", started = resA },
                    new { sagaId = sagaB, orderId = Guid.NewGuid(), label = "Cart_B", started = resB },
                },
            });
        }

        var ok = await PostStartCheckoutAsync(sagaId, orderId, demoItems, idempotencyKey, ct);
        if (!ok)
        {
            return StatusCode(502, new { sessionId = sagaId, status = "OrchestratorUnreachable" });
        }

        return Accepted(new
        {
            sessionId = sagaId,
            orderId,
            status = "Started",
            subscriptionToken = "demo-token",
        });
    }

    [HttpGet("saga/{sessionId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSagaStatus(Guid sessionId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(BackendClients.Checkout);
        try
        {
            using var resp = await client.GetAsync($"/api/checkouts/{sessionId}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound(new { sessionId, status = "NotFound" });
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("CheckoutOrchestrator returned {Status} for saga {SagaId}",
                    resp.StatusCode, sessionId);
                return StatusCode((int)resp.StatusCode, new { sessionId, status = "OrchestratorError" });
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var currentState = root.TryGetProperty("currentState", out var s) ? s.GetString() : null;
            var orderIdEl = root.TryGetProperty("orderId", out var o) ? o.GetGuid() : Guid.Empty;
            var failureReason = root.TryGetProperty("failureReason", out var f) && f.ValueKind != JsonValueKind.Null
                ? f.GetString() : null;
            var paymentUrl = root.TryGetProperty("paymentCheckoutUrl", out var u) && u.ValueKind != JsonValueKind.Null
                ? u.GetString() : null;

            return Ok(new
            {
                sessionId,
                orderId = orderIdEl,
                status = currentState ?? "Unknown",
                isComplete = currentState is "Completed" or "RequiresReview",
                isFailed = currentState == "Abandoned",
                failureReason,
                paymentCheckoutUrl = paymentUrl,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query CheckoutOrchestrator for saga {SagaId}", sessionId);
            return StatusCode(503, new { sessionId, status = "OrchestratorUnreachable" });
        }
    }

    private async Task<bool> PostStartCheckoutAsync(
        Guid sagaId, Guid orderId, CheckoutItemData[] items, string idempotencyKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(BackendClients.Checkout);
        try
        {
            // Shape matches Haworks.CheckoutOrchestrator.Api.Models.StartCheckoutRequest.
            // BffWeb doesn't reference the orchestrator's API project (would couple
            // BFF to a sibling-service implementation type, ADR-0001 boundary), so
            // we hand-shape the payload — anonymous object serialised by STJ camelCase.
            var payload = new
            {
                sagaId,
                orderId,
                userId = "demo-user",
                customerEmail = "demo@haworks.dev",
                totalAmount = items.Sum(i => i.UnitPrice * i.Quantity),
                idempotencyKey,
                items,
            };
            using var resp = await client.PostAsJsonAsync("/api/checkouts", payload, ct);
            if (resp.IsSuccessStatusCode) return true;

            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "CheckoutOrchestrator rejected demo start: {Status} {Body}",
                resp.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostStartCheckoutAsync failed for saga {SagaId}", sagaId);
            return false;
        }
    }

    private async Task<(bool A, bool B)> StartCartsRaceAsync(
        Guid sagaA, Guid sagaB, Guid orderA,
        CheckoutItemData[] items, string idempotencyKey, CancellationToken ct)
    {
        var orderB = Guid.NewGuid();
        var idempA = idempotencyKey + "-A";
        var idempB = idempotencyKey + "-B";
        var taskA = PostStartCheckoutAsync(sagaA, orderA, items, idempA, ct);
        var taskB = PostStartCheckoutAsync(sagaB, orderB, items, idempB, ct);
        await Task.WhenAll(taskA, taskB);
        return (await taskA, await taskB);
    }

    // ========================================================================
    // Event Flow (Outbox Pattern) — T2.5: real round-trip via payments outbox
    // ========================================================================
    //
    // BffWeb POSTs to payments-svc /admin/demo-event. Payments begins a
    // transaction on PaymentDbContext, publishes a DemoOutboxEvent via
    // its EF outbox (commits atomically), and the MT relay drains the
    // outbox row to RabbitMQ. BffWeb's DemoOutboxEventConsumer translates
    // the inbound message back to OnEventFlow stage='consumed' so the
    // frontend animates the full persisted -> consumed lifecycle against
    // the real EF outbox + broker plumbing.
    //
    // 'relayed' intermediate stage isn't emitted: would need
    // IDemoHubNotifier injection into MT's outbox dispatcher
    // (BuildingBlocks/Messaging plumbing). Tracked as follow-up.

    [HttpPost("events/trigger")]
    [AllowAnonymous]
    public async Task<IActionResult> TriggerEvent(
        [FromBody] EventTriggerRequest request,
        [FromHeader(Name = "X-Demo-Session")] Guid? demoSession,
        CancellationToken ct)
    {
        // The frontend's per-page demo session lives in the X-Demo-Session
        // header; subscribe + publish must agree on this id or the SignalR
        // push lands in a group nobody is listening to. Generate a fresh
        // one only if the caller didn't supply (preserves cURL-style use).
        var sessionId = demoSession is { } id && id != Guid.Empty ? id : Guid.NewGuid();

        // Always publish through payments-svc. The OutboxMessage row commits
        // atomically with the demo-event handler's transaction. If the relay
        // is paused (via /admin/relay-pause) the row stays in the outbox and
        // the 'consumed' SignalR push won't fire until /admin/relay-resume.
        var client = _httpClientFactory.CreateClient(BackendClients.Payments);
        var payload = new
        {
            sessionId,
            payload = request.Payload?.ToString() ?? "{}",
        };

        try
        {
            using var resp = await client.PostAsJsonAsync("/admin/demo-event", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("payments admin/demo-event returned {Status}", resp.StatusCode);
                return StatusCode((int)resp.StatusCode, new { sessionId, status = "PaymentsRejected" });
            }

            // Payments returns the EventId it stamped on the outbox row.
            // We re-emit the 'persisted' stage with that id so the
            // 'consumed' notification (driven by DemoOutboxEventConsumer)
            // matches it on the frontend's session timeline.
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var eventId = doc.RootElement.GetProperty("eventId").GetGuid();

            await _notifier.NotifyEventFlowAsync(new EventFlowEvent(
                sessionId, eventId.ToString(), "persisted", request.Payload?.ToString(), DateTime.UtcNow), ct);

            return Ok(new { sessionId, eventId, status = "Persisted" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "events/trigger: payments-svc unreachable");
            return StatusCode(503, new { sessionId, status = "PaymentsUnreachable" });
        }
    }

    [HttpGet("events/relay-status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetRelayStatus(CancellationToken ct)
    {
        // Reads real RelayPauseGate flag + live OutboxMessage row count
        // from payments-svc. The queuedCount is the number of undelivered
        // outbox rows in the payments DB — if the relay is paused, this
        // grows with each demo-event publish; on resume it drops to 0
        // within ~1s as BusOutboxDeliveryService drains.
        var client = _httpClientFactory.CreateClient(BackendClients.Payments);
        try
        {
            using var resp = await client.GetAsync("/admin/relay-status", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return Ok(new
            {
                isPaused = body.GetProperty("paused").GetBoolean(),
                queuedCount = body.GetProperty("queuedMessages").GetInt32(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "events/relay-status: payments-svc unreachable");
            return StatusCode(503, new { isPaused = false, queuedCount = 0, error = "payments unreachable" });
        }
    }

    [HttpPost("events/relay-pause")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetRelayPause([FromBody] RelayPauseRequest request, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(BackendClients.Payments);
        var endpoint = request.Paused ? "/admin/relay-pause" : "/admin/relay-resume";
        try
        {
            using var resp = await client.PostAsync(endpoint, content: null, ct);
            resp.EnsureSuccessStatusCode();

            // Re-read status to get the live drained count after resume.
            using var statusResp = await client.GetAsync("/admin/relay-status", ct);
            statusResp.EnsureSuccessStatusCode();
            var status = await statusResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            return Ok(new
            {
                isPaused = status.GetProperty("paused").GetBoolean(),
                queuedCount = status.GetProperty("queuedMessages").GetInt32(),
                drained = 0, // post-resume drain happens async via BusOutboxDeliveryService
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "events/relay-pause: payments-svc unreachable");
            return StatusCode(503, new { isPaused = request.Paused, queuedCount = 0, error = "payments unreachable" });
        }
    }

    // ========================================================================
    // Circuit Breaker — T2.3: real Polly circuit on real HTTP to catalog-svc
    // ========================================================================
    //
    // The circuit lives statically because Polly's state IS the demo —
    // open/closed/half-open transitions across requests. shouldFail=true
    // hits catalog-svc's /demo/fail (always 503); shouldFail=false hits
    // /health (always 2xx). After 2 consecutive 503s the circuit opens for
    // 6s; subsequent calls fail fast with BrokenCircuitException without
    // hitting catalog at all. After the break window, the next call is
    // half-open — success closes, another failure re-opens.
    private static readonly AsyncCircuitBreakerPolicy<HttpResponseMessage> s_circuit =
        Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>()
            .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: 2,
                                  durationOfBreak: TimeSpan.FromSeconds(6));

    // Process-wide counters the frontend reads to render the demo's metric
    // tiles. Reset on circuit/reset.
    private static int s_circuitSuccess;
    private static int s_circuitFailure;
    private static int s_circuitRejected;

    [HttpPost("circuit/request")]
    [AllowAnonymous]
    public async Task<IActionResult> CircuitRequest([FromBody] CircuitRequest request)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid();
        var client = _httpClientFactory.CreateClient(BackendClients.CatalogDemo);
        var path = request.ShouldFail ? "/demo/fail" : "/demo/health-with-chaos";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Bypass path: skip the shared static breaker entirely so the
            // caller observes raw upstream behaviour (success or full timeout)
            // regardless of whether the breaker is open from concurrent traffic.
            // The breaker's counters are NOT touched on the bypass path —
            // bypassed calls don't trip or reset the circuit.
            using var resp = request.BypassBreaker
                ? await client.GetAsync(path)
                : await s_circuit.ExecuteAsync(() => client.GetAsync(path));
            sw.Stop();
            var stateAfter = MapState(s_circuit.CircuitState);

            // Capture upstream replica identifier so the portfolio-site
            // receipt strip can show "catalog-svc-7e3f" alongside the BFF's
            // own X-Instance-Id. Aspire's WithReplicas(2) on catalog-svc
            // means this header value rotates between requests as the
            // proxy load-balances.
            var upstreamInstance = resp.Headers.TryGetValues("X-Instance-Id", out var ids)
                ? ids.FirstOrDefault()
                : null;

            if (resp.IsSuccessStatusCode) Interlocked.Increment(ref s_circuitSuccess);
            else Interlocked.Increment(ref s_circuitFailure);

            await _notifier.NotifyCircuitBreakerStateAsync(new CircuitBreakerStateEvent(
                sessionId, "catalog-svc", stateAfter, DateTime.UtcNow));

            return Ok(new
            {
                sessionId,
                success = resp.IsSuccessStatusCode,
                circuitState = stateAfter,
                statusCode = (int)resp.StatusCode,
                isRejected = false,
                failureCount = s_circuitFailure,
                successCount = s_circuitSuccess,
                rejectedCount = s_circuitRejected,
                responseTimeMs = sw.ElapsedMilliseconds,
                upstreamInstance,
                message = resp.IsSuccessStatusCode ? "OK" : $"Upstream {(int)resp.StatusCode}",
            });
        }
        catch (BrokenCircuitException)
        {
            sw.Stop();
            Interlocked.Increment(ref s_circuitRejected);
            await _notifier.NotifyCircuitBreakerStateAsync(new CircuitBreakerStateEvent(
                sessionId, "catalog-svc", "open", DateTime.UtcNow));
            return Ok(new
            {
                sessionId,
                success = false,
                circuitState = "open",
                isRejected = true,
                failureCount = s_circuitFailure,
                successCount = s_circuitSuccess,
                rejectedCount = s_circuitRejected,
                responseTimeMs = sw.ElapsedMilliseconds,
                retryAfterSeconds = 6,
                message = "Circuit open — fail-fast",
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            Interlocked.Increment(ref s_circuitFailure);
            _logger.LogWarning(ex, "Circuit demo: catalog-svc call threw {Type}", ex.GetType().Name);
            return Ok(new
            {
                sessionId,
                success = false,
                circuitState = MapState(s_circuit.CircuitState),
                isRejected = false,
                failureCount = s_circuitFailure,
                successCount = s_circuitSuccess,
                rejectedCount = s_circuitRejected,
                responseTimeMs = sw.ElapsedMilliseconds,
                message = ex.Message,
            });
        }
    }

    [HttpPost("circuit/toggle-failure")]
    [AllowAnonymous]
    public async Task<IActionResult> ToggleCircuitFailure([FromBody] ToggleFailureRequest request, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(BackendClients.CatalogDemo);
        try
        {
            if (request.FailureMode)
            {
                using var resp = await client.PostAsJsonAsync("/demo/chaos/trigger", new ChaosRequest("circuit-breaker-demo", 60), ct);
                resp.EnsureSuccessStatusCode();
            }
            else
            {
                using var resp = await client.PostAsync("/demo/chaos/clear", null, ct);
                resp.EnsureSuccessStatusCode();
            }
            return Ok(new { sessionId = request.SessionId, failureMode = request.FailureMode });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Circuit toggle failed: catalog-svc unreachable");
            return StatusCode(503, new { sessionId = request.SessionId, error = "catalog unreachable" });
        }
    }

    [HttpPost("circuit/reset")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetCircuit([FromBody] ResetRequest request)
    {
        // Polly's manual Reset() returns the circuit to Closed regardless of
        // current state. The demo's "Manual_Reset" button uses this to skip
        // the half-open dance.
        s_circuit.Reset();
        Interlocked.Exchange(ref s_circuitSuccess, 0);
        Interlocked.Exchange(ref s_circuitFailure, 0);
        Interlocked.Exchange(ref s_circuitRejected, 0);
        await _notifier.NotifyCircuitBreakerStateAsync(new CircuitBreakerStateEvent(
            request.SessionId, "catalog-svc", "closed", DateTime.UtcNow));
        return Ok(new
        {
            sessionId = request.SessionId,
            circuitState = "closed",
            failureCount = 0,
            successCount = 0,
            rejectedCount = 0,
        });
    }

    // Maps Polly's CircuitState enum to the kebab-case strings the frontend expects.
    private static string MapState(CircuitState state) => state switch
    {
        CircuitState.Closed   => "closed",
        CircuitState.Open     => "open",
        CircuitState.HalfOpen => "half-open",
        CircuitState.Isolated => "open", // manually isolated; demo doesn't distinguish
        _                     => "closed",
    };

    // ========================================================================
    // Vault / Secrets — proxies to identity-svc which makes the real
    // vault round-trip via IVaultService. No fallback / no static state:
    // pausing the vault container surfaces here as a real 5xx so the
    // topology auto-prober flips the corresponding card to broken.
    // ========================================================================

    [HttpGet("vault/status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetVaultStatus(CancellationToken ct)
    {
        // Honest passthrough — no static fallback. If identity (or its
        // upstream Vault) is unreachable, the visitor sees a real 5xx in
        // the topology auto-prober. This used to fall back to in-memory
        // s_vaultVersion / s_vaultLeaseExpiry which made docker-pausing
        // the vault container look like the system was still healthy.
        var client = _httpClientFactory.CreateClient(BackendClients.Identity);
        try
        {
            using var resp = await client.GetAsync("/admin/vault/status", ct);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
            return resp.IsSuccessStatusCode
                ? Ok(body)
                : StatusCode((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity unreachable for vault status");
            return StatusCode(503, new
            {
                status = "Unreachable",
                error = ex.GetType().Name,
                message = ex.Message,
            });
        }
    }

    [HttpPost("vault/rotate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RotateVault(
        [FromHeader(Name = "X-Demo-Session")] Guid? demoSession,
        CancellationToken ct)
    {
        // Carry the frontend's session id through to identity so the
        // VaultRotationStageEvent publishes are tagged with the same id
        // the browser is subscribed to.
        var sessionId = demoSession is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        var client = _httpClientFactory.CreateClient(BackendClients.Identity);
        try
        {
            using var resp = await client.PostAsync($"/admin/vault/rotate-credentials?roleName=haworks-identity&sessionId={sessionId}", content: null, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Identity vault-rotate returned {Status}", resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Identity vault-rotate request failed");
        }

        return Ok(new { sessionId, status = "Rotating" });
    }

    // ========================================================================
    // Idempotency — REAL in-process via DemoStateStore
    // ========================================================================

    // Idempotency demo: proxies to orders-svc /demo/idempotency/* which
    // exposes the SAME mechanism the production Orders aggregate uses —
    // a Postgres UNIQUE constraint on the key column with INSERT...ON
    // CONFLICT for atomic dedup. No in-process state, no fake. Pause
    // postgres via the topology chaos and these endpoints return real
    // 503s from the orders-svc -> postgres failure cascade.

    [HttpPost("idempotency/process")]
    [AllowAnonymous]
    public async Task<IActionResult> ProcessIdempotent(
        [FromHeader(Name = "X-Idempotency-Key")] string key,
        [FromHeader(Name = "X-Idempotency-Ttl-Seconds")] int? ttlSeconds,
        [FromHeader(Name = "X-Demo-Session")] Guid? demoSession,
        [FromBody] object payload,
        CancellationToken ct = default)
    {
        var sessionId = demoSession is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        if (string.IsNullOrEmpty(key)) return BadRequest("Missing idempotency key");

        var ttl = Math.Clamp(ttlSeconds ?? 30, 5, 600);

        var client = _httpClientFactory.CreateClient(BackendClients.Orders);
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"/demo/idempotency/claim?ttlSeconds={ttl}");
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", key);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return StatusCode((int)resp.StatusCode, new
                {
                    error = "orders idempotency claim failed",
                    statusCode = (int)resp.StatusCode,
                });
            }
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var isWinner = body.GetProperty("isWinner").GetBoolean();

            await _notifier.NotifyConcurrencyEventAsync(new ConcurrencyEvent(
                sessionId, "idempotency_check", key,
                isWinner ? "new" : "duplicate", 0, DateTime.UtcNow), ct);

            // Re-shape orders' response into the wire format the portfolio's
            // IdempotencyDemo expects (orderId in result, sessionId at top).
            var claimId = body.GetProperty("claimId").GetGuid();
            var keyInfo = body.GetProperty("keyInfo");
            return Ok(new
            {
                sessionId,
                idempotencyKey = key,
                isDuplicate = !isWinner,
                isWinner,
                result = new
                {
                    orderId = claimId,
                    status = isWinner ? "Created" : "Existing",
                },
                keyInfo = new
                {
                    createdAt = keyInfo.GetProperty("createdAt").GetDateTime(),
                    expiresAt = keyInfo.GetProperty("expiresAt").GetDateTime(),
                    ttlSeconds = keyInfo.GetProperty("ttlSeconds").GetInt32(),
                },
                cacheAgeSeconds = body.GetProperty("cacheAgeSeconds").GetInt32(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency: orders-svc unreachable");
            return StatusCode(503, new { error = "orders unreachable" });
        }
    }

    [HttpGet("idempotency/key/{key}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetIdempotencyStatus(string key, CancellationToken ct)
    {
        // GET status hits the same upstream mechanism by attempting a no-op
        // claim with TTL=5 — orders' ON CONFLICT returns the existing row
        // with isWinner=false if the key already exists. Cheap and honest;
        // no separate "lookup" endpoint needed.
        var client = _httpClientFactory.CreateClient(BackendClients.Orders);
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, "/demo/idempotency/claim?ttlSeconds=5");
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", key);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return StatusCode((int)resp.StatusCode, new { key, exists = false });
            }
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var isWinner = body.GetProperty("isWinner").GetBoolean();
            return Ok(new
            {
                key,
                exists = !isWinner,
                cacheAgeSeconds = body.GetProperty("cacheAgeSeconds").GetInt32(),
                result = new
                {
                    OrderId = body.GetProperty("claimId").GetGuid(),
                    Status = "Created",
                    CreatedAt = body.GetProperty("keyInfo").GetProperty("createdAt").GetDateTime(),
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency status: orders-svc unreachable");
            return StatusCode(503, new { key, exists = false, error = "orders unreachable" });
        }
    }

    [HttpPost("idempotency/race")]
    [AllowAnonymous]
    public async Task<IActionResult> ProcessIdempotencyRace(
        [FromBody] IdempotencyRaceRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Key)) return BadRequest("Missing key");

        // Race endpoint lives on orders too — fires N parallel claims into
        // a real Postgres UNIQUE constraint and reports who won.
        var client = _httpClientFactory.CreateClient(BackendClients.Orders);
        try
        {
            using var resp = await client.PostAsJsonAsync("/demo/idempotency/race", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return StatusCode((int)resp.StatusCode, new { error = "orders race failed" });
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency race: orders-svc unreachable");
            return StatusCode(503, new { error = "orders unreachable" });
        }
    }

    // ========================================================================
    // Cache Stampede + Invalidation — PHASE 2 will use catalog HybridCache
    // ========================================================================

    [HttpPost("cache/stampede")]
    [AllowAnonymous]
    public async Task<IActionResult> SimulateStampede([FromBody] StampedeRequest request, CancellationToken ct)
    {
        // T2.6: real cache-stampede demo via catalog-svc HybridCache.
        // catalog's /demo/cache-stampede runs Parallel.ForEachAsync of N
        // GetOrCreateAsync calls; HybridCache's singleflight collapses
        // them to 1 factory invocation (vs 'none' which bypasses cache and
        // runs the factory N times).
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);
        try
        {
            using var resp = await client.PostAsJsonAsync("/demo/cache-stampede", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return StatusCode((int)resp.StatusCode, new { error = "catalog stampede demo failed" });
            }
            // Forward catalog's response shape verbatim — fields match what
            // the frontend expects.
            var json = await resp.Content.ReadAsStringAsync(ct);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cache/stampede: catalog-svc unreachable");
            return StatusCode(503, new { sessionId = Guid.NewGuid(), error = "catalog unreachable" });
        }
    }

    // ----- Cache invalidation demo (#1+#2) — real catalog-svc round-trip ----
    // GET     /api/demo/cache/product/demo  -> POST catalog /demo/cache/seed-demo-product
    //                                          (idempotent find-or-create against real Postgres)
    // GET     /api/demo/cache/product/{id}  -> GET  catalog /api/products/{id}/cached
    //                                          (read-through HybridCache over real IProductRepository)
    // PUT     /api/demo/cache/product/{id}  -> PUT  catalog /api/products/{id}
    //                                          (real UpdateProductCommand: writes to Postgres,
    //                                           invalidates HybridCache, publishes
    //                                           ProductCacheInvalidatedEvent through EF outbox)
    // DELETE  /api/demo/cache/product/{id}  -> DELETE catalog /api/products/{id}
    //                                          (real DeleteProductCommand: same flow)
    //
    // The publish lands in catalog's outbox table inside the same transaction
    // as the row write; MassTransit BusOutboxDeliveryService relays to RabbitMQ;
    // BffWeb's ProductCacheInvalidatedBridge consumer translates to OnCacheEvent
    // for the demo session id passed as CorrelationId.
    //
    // Wire shape (CachedProductResponse) matches portfolio-site demo-client.ts.

    [HttpGet("cache/product/demo")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDemoProduct(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);
        try
        {
            using var resp = await client.PostAsync("/demo/cache/seed-demo-product", content: null, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return Ok(new { id = body.GetProperty("productId").GetGuid().ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cache/product/demo: catalog seed unreachable");
            return StatusCode(503, new { error = "catalog unreachable" });
        }
    }

    [HttpGet("cache/product/{productId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCachedProduct(Guid productId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);
        try
        {
            using var resp = await client.GetAsync($"/api/products/{productId}/cached", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound(new { productId });
            }
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return Ok(BuildCachedProductResponse(body, sessionId: null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cache/product/{ProductId}: catalog unreachable", productId);
            return StatusCode(503, new { error = "catalog unreachable", productId });
        }
    }

    [HttpPut("cache/product/{productId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> UpdateProduct(
        Guid productId,
        [FromBody] UpdateProductRequest request,
        CancellationToken ct)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid();
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);

        // Need name + categoryId for the real UpdateProductCommand. Re-read the
        // current product to fill in fields the frontend doesn't send (it only
        // sends price + name + sessionId; categoryId stays the same as current).
        try
        {
            using var getResp = await client.GetAsync($"/api/products/{productId}", ct);
            if (getResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound(new { productId });
            }
            getResp.EnsureSuccessStatusCode();
            var current = await getResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            var putBody = new
            {
                name = request.Name ?? current.GetProperty("name").GetString() ?? "Demo Widget",
                description = current.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                unitPrice = request.Price ?? current.GetProperty("unitPrice").GetDecimal(),
                categoryId = current.GetProperty("categoryId").GetGuid(),
                isListed = current.TryGetProperty("isListed", out var l) ? l.GetBoolean() : true,
                correlationId = sessionId,
            };

            using var putResp = await client.PutAsJsonAsync($"/api/products/{productId}", putBody, ct);
            putResp.EnsureSuccessStatusCode();

            // Re-read via the cached endpoint so the frontend gets current
            // cacheInfo (will be a miss because we just invalidated — that's
            // exactly what the demo wants to show).
            using var cachedResp = await client.GetAsync($"/api/products/{productId}/cached", ct);
            cachedResp.EnsureSuccessStatusCode();
            var cachedBody = await cachedResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            var response = BuildCachedProductResponse(cachedBody, sessionId);
            return Ok(new
            {
                response.sessionId,
                response.product,
                response.cacheInfo,
                invalidation = new
                {
                    cacheKeysInvalidated = new[] { $"product:{productId}" },
                    pubsubMessageSent = true,
                    instancesNotified = 1,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cache/product/{ProductId} PUT: catalog unreachable", productId);
            return StatusCode(503, new { sessionId, error = "catalog unreachable" });
        }
    }

    [HttpDelete("cache/product/{productId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> InvalidateCache(
        Guid productId,
        [FromQuery] Guid sessionId,
        CancellationToken ct)
    {
        var resolvedSession = sessionId == Guid.Empty ? Guid.NewGuid() : sessionId;
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);
        try
        {
            using var resp = await client.DeleteAsync(
                $"/api/products/{productId}?correlationId={resolvedSession}", ct);
            // 204 NoContent is the success response from a Result-pattern DELETE.
            // 404 means the product was already gone — still report invalidated.
            return Ok(new
            {
                sessionId = resolvedSession,
                invalidated = true,
                cacheKey = $"product:{productId}",
                pubsubMessageSent = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cache/product/{ProductId} DELETE: catalog unreachable", productId);
            return StatusCode(503, new { sessionId = resolvedSession, error = "catalog unreachable" });
        }
    }

    // Map catalog's `{ product, source, latencyMs }` shape to the frontend's
    // CachedProductResponse (product + cacheInfo). cachedAt / ttlSeconds /
    // totalTtlSeconds are the demo's animation metadata; HybridCache's
    // default TTL is 5 minutes so totalTtlSeconds=300 is honest.
    public sealed record CachedProductDto(Guid sessionId, object product, object cacheInfo);

    private static CachedProductDto BuildCachedProductResponse(JsonElement body, Guid? sessionId)
    {
        var source = body.GetProperty("source").GetString() ?? "database";
        var isHit = source == "L1" || source == "L2";
        JsonElement productElement = body.GetProperty("product");
        return new CachedProductDto(
            sessionId ?? Guid.NewGuid(),
            new
            {
                id = productElement.GetProperty("id").GetGuid(),
                name = productElement.GetProperty("name").GetString(),
                price = productElement.GetProperty("unitPrice").GetDecimal(),
                version = 1, // Catalog's ProductDto has no version field today; xmin lives server-side
            },
            new
            {
                isHit,
                source,
                cachedAt = DateTime.UtcNow,
                ttlSeconds = isHit ? 300 : 0,
                totalTtlSeconds = 300,
            });
    }

    public sealed record UpdateProductRequest(string? Name, decimal? Price, Guid? SessionId);

    // ========================================================================
    // Optimistic Concurrency — proxies to catalog-svc /demo/inventory/*
    // which uses Postgres' xmin column as the EF concurrency token (the
    // SAME mechanism the production ReserveStockCommand depends on).
    // Pause postgres / catalog via the topology chaos panel and these
    // endpoints surface as real 503/504s instead of phantom in-memory
    // counters incrementing forever.
    // ========================================================================

    [HttpGet("inventory/{inventoryId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetInventory(Guid inventoryId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);
        try
        {
            using var resp = await client.GetAsync($"/demo/inventory/{inventoryId}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return NotFound(new { inventoryId });
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory get: catalog-svc unreachable");
            return StatusCode(503, new { error = "catalog unreachable", inventoryId });
        }
    }

    [HttpPut("inventory/{inventoryId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> UpdateInventory(
        Guid inventoryId,
        [FromBody] InventoryUpdate update,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromHeader(Name = "X-Demo-Session")] Guid? demoSession,
        CancellationToken ct = default)
    {
        var sessionId = demoSession is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);
        try
        {
            // Catalog's /demo/inventory/{id} expects raw xmin in If-Match
            // (no ETag quoting). Strip surrounding quotes the frontend may
            // send via the standard ETag wire shape.
            var normalisedIfMatch = ifMatch?.Trim('"');
            using var req = new HttpRequestMessage(HttpMethod.Put, $"/demo/inventory/{inventoryId}")
            {
                Content = JsonContent.Create(update),
            };
            if (!string.IsNullOrEmpty(normalisedIfMatch))
            {
                req.Headers.TryAddWithoutValidation("If-Match", normalisedIfMatch);
            }

            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.PreconditionFailed
                || resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                await _notifier.NotifyConcurrencyEventAsync(new ConcurrencyEvent(
                    sessionId, "update_inventory", inventoryId.ToString(),
                    "conflict", 0, DateTime.UtcNow), ct);
                // Pass through catalog's response body — it includes the
                // current version + quantity so the client can retry.
                return StatusCode((int)resp.StatusCode, System.Text.Json.JsonSerializer.Deserialize<JsonElement>(body));
            }

            if (!resp.IsSuccessStatusCode)
            {
                return StatusCode((int)resp.StatusCode,
                    new { error = "catalog inventory update failed" });
            }

            await _notifier.NotifyConcurrencyEventAsync(new ConcurrencyEvent(
                sessionId, "update_inventory", inventoryId.ToString(),
                "success", 0, DateTime.UtcNow), ct);

            return Content(body, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory update: catalog-svc unreachable");
            return StatusCode(503, new { error = "catalog unreachable", inventoryId });
        }
    }

    // ========================================================================
    // Rate Limiting — REAL in-process (DemoStateStore.RateLimiters)
    // ========================================================================

    [HttpPost("ratelimit/configure")]
    [AllowAnonymous]
    public IActionResult ConfigureRateLimit([FromBody] RateLimitConfig config)
    {
        var sessionId = config.SessionId ?? Guid.NewGuid();
        _stateStore.GetOrCreateLimiter(sessionId, config.PermitLimit, config.WindowSeconds);
        return Ok(new { sessionId });
    }

    [HttpPost("ratelimit/request")]
    [AllowAnonymous]
    public async Task<IActionResult> RateLimitRequest(
        [FromBody] SessionRequest request,
        [FromHeader(Name = "X-Demo-Session")] Guid? demoSession)
    {
        var sessionId = request.SessionId ?? (demoSession is { } id && id != Guid.Empty ? id : Guid.NewGuid());
        const int Limit = 5;
        var limiter = _stateStore.GetOrCreateLimiter(sessionId, Limit, 60);

        using var lease = await limiter.AcquireAsync(1);
        var permits = limiter.GetStatistics();
        var remaining = (int)(permits?.CurrentAvailablePermits ?? 0);
        var retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? (int?)retryAfter.TotalSeconds : null;
        var resetAt = DateTime.UtcNow.AddSeconds(retryAfterSeconds ?? 60);

        await _notifier.NotifyRateLimitAsync(new RateLimitEvent(
            sessionId, lease.IsAcquired, remaining, retryAfterSeconds, DateTime.UtcNow));

        // Wire shape matches portfolio-site RateLimitResponse:
        //   { sessionId, allowed, bucket: { remaining, limit, resetAt,
        //     retryAfterSeconds }, requestNumber? }
        return Ok(new
        {
            sessionId,
            allowed = lease.IsAcquired,
            bucket = new
            {
                remaining,
                limit = Limit,
                resetAt,
                retryAfterSeconds,
            },
        });
    }

    [HttpPost("ratelimit/burst")]
    [AllowAnonymous]
    public async Task<IActionResult> RateLimitBurst(
        [FromBody] BurstRequest request,
        [FromHeader(Name = "X-Demo-Session")] Guid? demoSession)
    {
        var sessionId = demoSession is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        var limiter = _stateStore.GetOrCreateLimiter(sessionId, 5, 60);

        var results = new List<object>(request.Count);
        var allowedCount = 0;

        for (var i = 0; i < request.Count; i++)
        {
            using var lease = await limiter.AcquireAsync(1);
            var permits = limiter.GetStatistics();
            var remaining = (int)(permits?.CurrentAvailablePermits ?? 0);
            int? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var meta)
                ? (int?)meta.TotalSeconds
                : null;

            await _notifier.NotifyRateLimitAsync(new RateLimitEvent(
                sessionId, lease.IsAcquired, remaining, retryAfter, DateTime.UtcNow));

            if (lease.IsAcquired) allowedCount++;

            results.Add(new
            {
                requestNumber = i + 1,
                allowed = lease.IsAcquired,
                remaining,
                retryAfter,
            });

            if (request.DelayMs > 0 && i < request.Count - 1)
            {
                await Task.Delay(request.DelayMs);
            }
        }

        return Ok(new
        {
            sessionId,
            results,
            summary = new
            {
                total = request.Count,
                allowed = allowedCount,
                rejected = request.Count - allowedCount,
            },
        });
    }

    // ========================================================================
    // Chaos / Fault Injection — PHASE 2 will set a Vault flag downstream services consult
    // ========================================================================

    [HttpPost("chaos/trigger")]
    [AllowAnonymous]
    public async Task<IActionResult> TriggerChaos([FromBody] ChaosRequest request, CancellationToken ct)
    {
        // T2.7: route through catalog-svc's /demo/chaos/trigger which sets
        // an in-process flag for N seconds. While the flag is set, catalog's
        // /demo/health-with-chaos returns 503 — combined with the circuit
        // breaker demo, the breaker opens from real upstream chaos rather
        // than the always-failing /demo/fail.
        //
        // Production version (Phase 2 brief T2.7): chaos flag in Vault KV
        // so all service instances + the BFF agree on state. Today's
        // single-process per-service flag is the dev/demo equivalent.
        var client = _httpClientFactory.CreateClient(BackendClients.Catalog);
        try
        {
            using var resp = await client.PostAsJsonAsync("/demo/chaos/trigger", request, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return Content(body, "application/json");
            }
            _logger.LogWarning("Catalog chaos/trigger returned {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog chaos/trigger unreachable; falling back to local log");
        }

        // Local-only fallback so the frontend always gets a trace_id.
        var traceId = Guid.NewGuid().ToString("N");
        _logger.LogWarning("CHAOS (local fallback): scenario={Scenario} duration={Duration}s trace={TraceId}",
            request.Scenario, request.DurationSeconds, traceId);
        return Ok(new { trace_id = traceId });
    }
}

// DTOs — wire shapes pinned by the frontend's TypeScript types
// (portfolio-site/src/lib/api/demo-client.ts).
public record ChaosRequest(string Scenario, int DurationSeconds);
public record SagaStartRequest(string ScenarioType, int SimulatedDelayMs);
public record EventTriggerRequest(string EventType, object Payload);
public record RelayPauseRequest(bool Paused);
public record IdempotencyRaceRequest(string Key, int Count, int? TtlSeconds);
public record RaceOutcome(int RequestIndex, bool IsWinner, Guid OrderId, long LatencyMs);
// `BypassBreaker` exists for the side-by-side "no-breaker baseline lane"
// the frontend's CircuitBreakerDemo renders. When true, the request goes
// directly through HttpClient without the static AsyncCircuitBreakerPolicy,
// so the caller can observe the timeout cliff a circuit-less system would
// experience. Default false preserves the original demo flow.
public record CircuitRequest(Guid? SessionId, bool ShouldFail, bool BypassBreaker = false);
public record ToggleFailureRequest(Guid SessionId, bool FailureMode);
public record ResetRequest(Guid SessionId);
public record StampedeRequest(int ConcurrentRequests, string CacheKey, string ProtectionMode, int SimulatedDbLatencyMs);
public record InventoryUpdate(int Quantity);
public record RateLimitConfig(Guid? SessionId, int PermitLimit, int WindowSeconds);
public record SessionRequest(Guid? SessionId);
public record BurstRequest(int Count, int DelayMs);
