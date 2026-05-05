using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Haworks.BffWeb.Api.Demo;
using Haworks.BffWeb.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
[AllowAnonymous]
public class DemoController : ControllerBase
{
    private readonly IDemoHubNotifier _notifier;
    private readonly IDemoTraceStore _traceStore;
    private readonly DemoStateStore _stateStore;
    private readonly ILogger<DemoController> _logger;

    public DemoController(
        IDemoHubNotifier notifier,
        IDemoTraceStore traceStore,
        DemoStateStore stateStore,
        ILogger<DemoController> logger)
    {
        _notifier = notifier;
        _traceStore = traceStore;
        _stateStore = stateStore;
        _logger = logger;
    }

    // ========================================================================
    // Distributed Tracing — synthesizes a structurally honest 7-span trace
    // ========================================================================

    [HttpPost("tracing/start")]
    public IActionResult StartTrace([FromBody] TraceStartRequest? request)
    {
        var scenario = request?.Scenario ?? "happyPath";
        var traceId = Guid.NewGuid().ToString("N");

        var spans = new List<DemoSpan>();
        var rootSpanId = NewSpanId();

        var rootDuration = scenario == "withFailure" ? 142 : 128;
        spans.Add(new DemoSpan(
            rootSpanId, null, "bff-web", "POST /api/demo/tracing/start",
            StartMs: 0, DurationMs: rootDuration, Status: "OK",
            new Dictionary<string, object>
            {
                ["http.method"] = "POST",
                ["http.route"] = "/api/demo/tracing/start",
                ["scenario"] = scenario,
            }));

        var validateId = NewSpanId();
        spans.Add(new DemoSpan(
            validateId, rootSpanId, "orders-svc", "validate-cart",
            StartMs: 4, DurationMs: 6, Status: "OK",
            new Dictionary<string, object> { ["cart.items"] = 1, ["cart.total"] = 39.99 }));

        var inventoryId = NewSpanId();
        spans.Add(new DemoSpan(
            inventoryId, rootSpanId, "catalog-svc", "reserve-stock",
            StartMs: 12, DurationMs: 18, Status: "OK",
            new Dictionary<string, object>
            {
                ["product.id"] = "demo-widget",
                ["quantity"] = 1,
                ["reservation.ttl"] = 30,
            }));

        var paymentsStart = 32;
        var paymentsDuration = scenario == "withFailure" ? 95 : 78;
        var paymentsId = NewSpanId();
        spans.Add(new DemoSpan(
            paymentsId, rootSpanId, "payments-svc", "create-session",
            StartMs: paymentsStart, DurationMs: paymentsDuration,
            Status: scenario == "withFailure" ? "Error" : "OK",
            new Dictionary<string, object>
            {
                ["payment.provider"] = "stripe",
                ["amount"] = 39.99,
                ["currency"] = "GBP",
            }));

        var stripeId = NewSpanId();
        spans.Add(new DemoSpan(
            stripeId, paymentsId, "external-stripe", "POST /v1/checkout/sessions",
            StartMs: paymentsStart + 4, DurationMs: paymentsDuration - 8,
            Status: scenario == "withFailure" ? "Error" : "OK",
            new Dictionary<string, object> { ["http.method"] = "POST", ["http.host"] = "api.stripe.com" }));

        var notifyId = NewSpanId();
        spans.Add(new DemoSpan(
            notifyId, rootSpanId, "checkout-orchestrator", "saga-step-paymentcompleted",
            StartMs: paymentsStart + paymentsDuration + 2, DurationMs: 4, Status: "OK",
            new Dictionary<string, object> { ["saga.state"] = "Completed" }));

        var outboxId = NewSpanId();
        spans.Add(new DemoSpan(
            outboxId, rootSpanId, "ef-outbox", "publish-OrderCompletedEvent",
            StartMs: paymentsStart + paymentsDuration + 6, DurationMs: 6, Status: "OK",
            new Dictionary<string, object>
            {
                ["event.type"] = "OrderCompletedEvent",
                ["broker"] = "rabbitmq",
            }));

        _traceStore.Record(new DemoTrace(traceId, rootSpanId, rootDuration, spans));
        Response.Headers["X-Trace-Id"] = traceId;

        return Ok(new { traceId, rootSpanId, durationMs = rootDuration, spanCount = spans.Count, scenario });
    }

    private static string NewSpanId() => Guid.NewGuid().ToString("N").Substring(0, 16);

    // ========================================================================
    // Saga (Checkout Flow) — PHASE 2 will route into CheckoutOrchestrator
    // ========================================================================

    [HttpPost("saga/start")]
    public Task<IActionResult> StartSaga([FromBody] SagaStartRequest request)
    {
        var sessionId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        _logger.LogInformation("Demo saga (stub): scenario={Scenario} sessionId={SessionId}",
            request.ScenarioType, sessionId);

        // PHASE 2: publish CheckoutInitiatedEvent via IPublishEndpoint with the
        // DemoScenario header so the saga consumes it and the saga's state
        // transitions stream back to the browser via the hub.
        if (request.ScenarioType == "stockRace")
        {
            var sagaA = Guid.NewGuid();
            var sagaB = Guid.NewGuid();
            return Task.FromResult<IActionResult>(Accepted(new
            {
                status = "RaceStarted",
                sessionId = sagaA,
                races = new[]
                {
                    new { sagaId = sagaA, orderId = Guid.NewGuid(), label = "Cart_A" },
                    new { sagaId = sagaB, orderId = Guid.NewGuid(), label = "Cart_B" },
                },
            }));
        }

        return Task.FromResult<IActionResult>(Accepted(new
        {
            sessionId,
            orderId,
            status = "Started",
            subscriptionToken = "demo-token",
        }));
    }

    [HttpGet("saga/{sessionId}")]
    public IActionResult GetSagaStatus(Guid sessionId)
    {
        // PHASE 2: query CheckoutOrchestrator's saga state by sessionId (SagaId).
        // Phase 1: return a plausible Completed shape so the UI doesn't 404.
        return Ok(new
        {
            sessionId,
            orderId = Guid.NewGuid(),
            status = "Completed",
            isComplete = true,
            isFailed = false,
        });
    }

    // ========================================================================
    // Event Flow (Outbox Pattern) — PHASE 2 will write to payments outbox
    // ========================================================================

    private static bool s_relayPaused;
    private static int s_relayQueued;

    [HttpPost("events/trigger")]
    public async Task<IActionResult> TriggerEvent([FromBody] EventTriggerRequest request)
    {
        var sessionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var payload = request.Payload?.ToString() ?? "{}";

        if (s_relayPaused)
        {
            Interlocked.Increment(ref s_relayQueued);
            await _notifier.NotifyEventFlowAsync(new EventFlowEvent(
                sessionId, eventId.ToString(), "persisted", payload, DateTime.UtcNow));
            return Ok(new
            {
                sessionId,
                eventId,
                status = "QueuedWhileRelayPaused",
                queuedCount = s_relayQueued,
            });
        }

        // PHASE 2: BeginTransactionAsync on payments DbContext, Publish, Commit.
        // EF outbox writes the OutboxMessage row in the same TX; relay drains it.
        await _notifier.NotifyEventFlowAsync(new EventFlowEvent(
            sessionId, eventId.ToString(), "persisted", payload, DateTime.UtcNow));

        // Simulate the relay + consumer stages so the UI animates the full flow.
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            await _notifier.NotifyEventFlowAsync(new EventFlowEvent(
                sessionId, eventId.ToString(), "relayed", payload, DateTime.UtcNow));
            await Task.Delay(120);
            await _notifier.NotifyEventFlowAsync(new EventFlowEvent(
                sessionId, eventId.ToString(), "consumed", payload, DateTime.UtcNow));
        });

        return Ok(new { sessionId, eventId, status = "Persisted" });
    }

    [HttpGet("events/relay-status")]
    public IActionResult GetRelayStatus() =>
        Ok(new { isPaused = s_relayPaused, queuedCount = s_relayQueued });

    [HttpPost("events/relay-pause")]
    public IActionResult SetRelayPause([FromBody] RelayPauseRequest request)
    {
        if (request.Paused)
        {
            s_relayPaused = true;
            return Ok(new { isPaused = true, queuedCount = s_relayQueued, drained = 0 });
        }
        var drained = Interlocked.Exchange(ref s_relayQueued, 0);
        s_relayPaused = false;
        return Ok(new { isPaused = false, queuedCount = 0, drained });
    }

    // ========================================================================
    // Circuit Breaker — PHASE 2 will route through typed HttpClient + Polly
    // ========================================================================

    private static int s_circuitFailureCount;
    private const int CircuitFailureThreshold = 2;

    [HttpPost("circuit/request")]
    public async Task<IActionResult> CircuitRequest([FromBody] CircuitRequest request)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid();

        // PHASE 2: BffWeb's typed HttpClient to catalog-svc with an explicit
        // Polly circuit. shouldFail=true hits a /demo/fail endpoint catalog
        // exposes that returns 503 — Polly opens the circuit after 2 failures.
        if (request.ShouldFail)
        {
            var failures = Interlocked.Increment(ref s_circuitFailureCount);
            var state = failures >= CircuitFailureThreshold ? "open" : "closed";
            await _notifier.NotifyCircuitBreakerStateAsync(new CircuitBreakerStateEvent(
                sessionId, "demo-service", state, DateTime.UtcNow));

            if (state == "open")
            {
                return Ok(new { sessionId, success = false, circuitState = "open", isRejected = true });
            }
            return Ok(new { sessionId, success = false, circuitState = "closed" });
        }

        // Successful call resets the failure count (matches Polly's behaviour
        // on the first successful call after a failure).
        Interlocked.Exchange(ref s_circuitFailureCount, 0);
        await _notifier.NotifyCircuitBreakerStateAsync(new CircuitBreakerStateEvent(
            sessionId, "demo-service", "closed", DateTime.UtcNow));
        return Ok(new { sessionId, success = true, circuitState = "closed" });
    }

    [HttpPost("circuit/toggle-failure")]
    public IActionResult ToggleCircuitFailure([FromBody] ToggleFailureRequest request) =>
        Ok(new { sessionId = request.SessionId });

    [HttpPost("circuit/reset")]
    public async Task<IActionResult> ResetCircuit([FromBody] ResetRequest request)
    {
        Interlocked.Exchange(ref s_circuitFailureCount, 0);
        await _notifier.NotifyCircuitBreakerStateAsync(new CircuitBreakerStateEvent(
            request.SessionId, "demo-service", "closed", DateTime.UtcNow));
        return Ok(new { sessionId = request.SessionId });
    }

    // ========================================================================
    // Vault / Secrets — PHASE 2 will trigger real Vault rotation via identity-svc
    // ========================================================================

    private static int s_vaultVersion = 1;
    private static DateTime s_vaultLeaseExpiry = DateTime.UtcNow.AddSeconds(3600);

    [HttpGet("vault/status")]
    public IActionResult GetVaultStatus()
    {
        var ttl = (int)Math.Max(0, (s_vaultLeaseExpiry - DateTime.UtcNow).TotalSeconds);
        return Ok(new
        {
            sessionId = Guid.NewGuid(),
            currentVersion = s_vaultVersion,
            status = ttl > 0 ? "Healthy" : "Expired",
            ttlSeconds = ttl,
        });
    }

    [HttpPost("vault/rotate")]
    public IActionResult RotateVault()
    {
        var sessionId = Guid.NewGuid();
        var previousVersion = s_vaultVersion;
        var newVersion = Interlocked.Increment(ref s_vaultVersion);
        s_vaultLeaseExpiry = DateTime.UtcNow.AddSeconds(3600);

        // PHASE 2: hit identity-svc's /admin/vault/rotate endpoint and stream
        // its real rotation stages (started -> activated -> grace_period -> revoked).
        _ = Task.Run(async () =>
        {
            await _notifier.NotifyVaultRotationAsync(new VaultRotationEvent(
                sessionId, "started", newVersion, previousVersion.ToString(), DateTime.UtcNow));
            await Task.Delay(150);
            await _notifier.NotifyVaultRotationAsync(new VaultRotationEvent(
                sessionId, "activated", newVersion, previousVersion.ToString(), DateTime.UtcNow));
            await Task.Delay(150);
            await _notifier.NotifyVaultRotationAsync(new VaultRotationEvent(
                sessionId, "grace_period", newVersion, previousVersion.ToString(), DateTime.UtcNow));
            await Task.Delay(300);
            await _notifier.NotifyVaultRotationAsync(new VaultRotationEvent(
                sessionId, "revoked", newVersion, previousVersion.ToString(), DateTime.UtcNow));
        });

        return Ok(new { sessionId, status = "Rotating" });
    }

    // ========================================================================
    // Idempotency — REAL in-process via DemoStateStore
    // ========================================================================

    [HttpPost("idempotency/process")]
    public async Task<IActionResult> ProcessIdempotent(
        [FromHeader(Name = "X-Idempotency-Key")] string key,
        [FromHeader(Name = "X-Idempotency-Ttl-Seconds")] int? ttlSeconds,
        [FromBody] object payload)
    {
        var sessionId = Guid.NewGuid();
        if (string.IsNullOrEmpty(key)) return BadRequest("Missing idempotency key");

        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromSeconds(Math.Clamp(ttlSeconds ?? 30, 5, 600));

        // Atomic claim. AddOrUpdate's update factory replaces an expired entry;
        // if the existing entry is still valid the loser receives it. Reference
        // equality on the returned entry tells us whether we won.
        var newEntry = new IdempotencyEntry(
            new IdempotencyResult(Guid.NewGuid(), "Created", now), now, now.Add(ttl));

        var settled = _stateStore.IdempotencyKeys.AddOrUpdate(
            key,
            newEntry,
            (_, prev) => prev.IsExpired(now) ? newEntry : prev);

        var isWinner = ReferenceEquals(settled, newEntry);

        await _notifier.NotifyConcurrencyEventAsync(new ConcurrencyEvent(
            sessionId, "idempotency_check", key, isWinner ? "new" : "duplicate", 0, now));

        return Ok(new
        {
            sessionId,
            result = settled.Result,
            isDuplicate = !isWinner,
            isWinner,
            cacheAgeSeconds = (int)(now - settled.CreatedAt).TotalSeconds,
            expiresInSeconds = (int)Math.Max(0, (settled.ExpiresAt - now).TotalSeconds),
            ttlSeconds = (int)ttl.TotalSeconds,
        });
    }

    [HttpGet("idempotency/key/{key}")]
    public IActionResult GetIdempotencyStatus(string key)
    {
        if (_stateStore.IdempotencyKeys.TryGetValue(key, out var entry))
        {
            var now = DateTime.UtcNow;
            return Ok(new
            {
                key,
                exists = !entry.IsExpired(now),
                expiresInSeconds = (int)Math.Max(0, (entry.ExpiresAt - now).TotalSeconds),
                cacheAgeSeconds = (int)(now - entry.CreatedAt).TotalSeconds,
                result = entry.Result,
            });
        }
        return Ok(new { key, exists = false });
    }

    [HttpPost("idempotency/race")]
    public IActionResult ProcessIdempotencyRace([FromBody] IdempotencyRaceRequest request)
    {
        if (string.IsNullOrEmpty(request.Key)) return BadRequest("Missing key");

        var count = Math.Clamp(request.Count, 2, 10);
        var ttl = TimeSpan.FromSeconds(Math.Clamp(request.TtlSeconds ?? 30, 5, 600));

        _stateStore.IdempotencyKeys.TryRemove(request.Key, out _);

        var outcomes = new ConcurrentBag<RaceOutcome>();
        Parallel.For(0, count, i =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var now = DateTime.UtcNow;
            var newEntry = new IdempotencyEntry(
                new IdempotencyResult(Guid.NewGuid(), "Created", now), now, now.Add(ttl));

            var settled = _stateStore.IdempotencyKeys.AddOrUpdate(
                request.Key,
                newEntry,
                (_, prev) => prev.IsExpired(now) ? newEntry : prev);

            sw.Stop();
            outcomes.Add(new RaceOutcome(
                i, ReferenceEquals(settled, newEntry), settled.Result.OrderId, sw.ElapsedMilliseconds));
        });

        return Ok(new
        {
            key = request.Key,
            count,
            ttlSeconds = (int)ttl.TotalSeconds,
            outcomes = outcomes.OrderBy(o => o.RequestIndex).ToArray(),
        });
    }

    // ========================================================================
    // Cache Stampede + Invalidation — PHASE 2 will use catalog HybridCache
    // ========================================================================

    [HttpPost("cache/stampede")]
    public IActionResult SimulateStampede([FromBody] StampedeRequest request)
    {
        // PHASE 2: drive real HybridCache.GetOrCreateAsync against catalog-svc
        // and count actual DB hits. Phase 1 returns the canned shape: with
        // singleflight protection, exactly 1 query serves N concurrent reads.
        var sessionId = Guid.NewGuid();
        var dbQueries = request.ProtectionMode == "singleflight" ? 1 : request.ConcurrentRequests;
        return Ok(new
        {
            sessionId,
            protectionMode = request.ProtectionMode,
            cacheHits = request.ConcurrentRequests - dbQueries,
            cacheMisses = dbQueries,
            dbQueries,
        });
    }

    [HttpGet("cache/product/demo")]
    public IActionResult GetDemoProduct() =>
        Ok(new { id = Guid.NewGuid() });

    [HttpGet("cache/product/{productId}")]
    public IActionResult GetCachedProduct(Guid productId) =>
        Ok(new
        {
            product = new { id = productId, name = "Demo Widget", price = 39.99m, version = 1 },
            cacheInfo = new { isHit = true, source = "Hybrid" },
        });

    [HttpPut("cache/product/{productId}")]
    public IActionResult UpdateProduct(Guid productId, [FromBody] object updates) =>
        Ok(new
        {
            sessionId = Guid.NewGuid(),
            invalidation = new
            {
                cacheKeysInvalidated = new[] { $"product:{productId}" },
                pubsubMessageSent = true,
            },
        });

    [HttpDelete("cache/product/{productId}")]
    public IActionResult InvalidateCache(Guid productId) =>
        Ok(new { invalidated = true, cacheKey = $"product:{productId}" });

    // ========================================================================
    // Optimistic Concurrency — REAL in-process (DemoStateStore.InventoryVersions)
    // ========================================================================

    [HttpGet("inventory/{inventoryId}")]
    public IActionResult GetInventory(string inventoryId)
    {
        var version = _stateStore.InventoryVersions.GetOrAdd(inventoryId, 1);
        return Ok(new
        {
            inventory = new { id = inventoryId, name = "Demo Stock", quantity = 100, version },
        });
    }

    [HttpPut("inventory/{inventoryId}")]
    public async Task<IActionResult> UpdateInventory(
        string inventoryId,
        [FromBody] InventoryUpdate update,
        [FromHeader(Name = "If-Match")] string ifMatch)
    {
        var sessionId = Guid.NewGuid();
        var currentVersion = _stateStore.InventoryVersions.GetOrAdd(inventoryId, 1);
        var expectedETag = $"\"{currentVersion}\"";

        if (ifMatch != expectedETag)
        {
            await _notifier.NotifyConcurrencyEventAsync(new ConcurrencyEvent(
                sessionId, "update_inventory", inventoryId, "conflict", currentVersion, DateTime.UtcNow));
            return Conflict(new
            {
                message = "Optimistic concurrency failure: Version mismatch",
                currentVersion,
            });
        }

        var newVersion = currentVersion + 1;
        _stateStore.InventoryVersions[inventoryId] = newVersion;

        await _notifier.NotifyConcurrencyEventAsync(new ConcurrencyEvent(
            sessionId, "update_inventory", inventoryId, "success", newVersion, DateTime.UtcNow));

        return Ok(new
        {
            inventory = new { id = inventoryId, name = "Demo Stock", quantity = update.Quantity, version = newVersion },
        });
    }

    // ========================================================================
    // Rate Limiting — REAL in-process (DemoStateStore.RateLimiters)
    // ========================================================================

    [HttpPost("ratelimit/configure")]
    public IActionResult ConfigureRateLimit([FromBody] RateLimitConfig config)
    {
        var sessionId = config.SessionId ?? Guid.NewGuid();
        _stateStore.GetOrCreateLimiter(sessionId, config.PermitLimit, config.WindowSeconds);
        return Ok(new { sessionId });
    }

    [HttpPost("ratelimit/request")]
    public async Task<IActionResult> RateLimitRequest([FromBody] SessionRequest request)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid();
        var limiter = _stateStore.GetOrCreateLimiter(sessionId, 5, 60);

        using var lease = await limiter.AcquireAsync(1);
        var permits = limiter.GetStatistics();
        var eventData = new RateLimitEvent(
            sessionId,
            lease.IsAcquired,
            (int)(permits?.CurrentAvailablePermits ?? 0),
            lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? (int?)retryAfter.TotalSeconds : null,
            DateTime.UtcNow);

        await _notifier.NotifyRateLimitAsync(eventData);

        if (lease.IsAcquired) return Ok(new { allowed = true, remaining = eventData.Remaining });
        return StatusCode(429, new { allowed = false, retryAfter = eventData.RetryAfterSeconds });
    }

    [HttpPost("ratelimit/burst")]
    public async Task<IActionResult> RateLimitBurst([FromBody] BurstRequest request)
    {
        var sessionId = Guid.NewGuid();
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
    public IActionResult TriggerChaos([FromBody] ChaosRequest request)
    {
        var traceId = Guid.NewGuid().ToString();
        _logger.LogWarning("CHAOS (stub): scenario={Scenario} duration={Duration}s trace={TraceId}",
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
public record TraceStartRequest(string? Scenario);
public record CircuitRequest(Guid? SessionId, bool ShouldFail);
public record ToggleFailureRequest(Guid SessionId, bool FailureMode);
public record ResetRequest(Guid SessionId);
public record StampedeRequest(int ConcurrentRequests, string CacheKey, string ProtectionMode, int SimulatedDbLatencyMs);
public record InventoryUpdate(int Quantity);
public record RateLimitConfig(Guid? SessionId, int PermitLimit, int WindowSeconds);
public record SessionRequest(Guid? SessionId);
public record BurstRequest(int Count, int DelayMs);
