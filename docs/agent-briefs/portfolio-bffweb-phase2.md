# Agent Brief — Portfolio Site BffWeb Demos: Phase 2

**Audience:** an autonomous coding agent (Gemini, Claude, etc.) with shell access to two repos and the ability to run `dotnet`/`docker`.
**Goal:** make every demo on portfolio-site exercise *real* cross-service traffic on `ritualworks-platform`. Phase 1 ported the demo surface onto BffWeb with three demos genuinely real and six returning correct shapes from in-process stubs. Phase 2 closes the gap.
**Definition of done per demo:** the handler does real work against the relevant downstream microservice (HTTP call or MassTransit publish), real state changes are visible in the platform's databases or RabbitMQ, and SignalR streams reflect the real progression. The wire-format response shape stays identical so the frontend doesn't change.

This brief is **self-contained** — you do not need access to any prior chat to follow it. Read top-to-bottom before starting.

---

## 1. The repos and current state

### Source of truth for "what real implementation looks like"
- Path: `/Users/chidionyema/Documents/code/ritualworks/`
- Branch: `feature/ha-portfolio-integration`
- This is the .NET 9 modular monolith. Its `DemoController` already does real cross-context work — the platform is splitting that monolith into microservices and BffWeb routes calls into the right ones.
- **Read-only reference.** Do not modify.

### Where Phase 2 work lands
- Path: `/Users/chidionyema/Documents/code/ritualworks-platform/`
- Branch off: `feat/portfolio-bffweb-demos` (where Phase 1 lives, possibly already merged to `main`)
- Each Phase 2 task is one PR off `main` (or `feat/portfolio-bffweb-demos` if it hasn't merged yet).

### Frontend contract
- Path: `/Users/chidionyema/Documents/code/portfolio-site/`
- Branch: `main`
- Calls into the backend live in:
  - `src/lib/api/demo-client.ts` — REST shapes for every endpoint
  - `src/lib/api/signalr.ts` — hub URL + event types
- **The wire format is the contract.** Phase 2 work must keep request/response shapes byte-identical; only the *implementation* changes from in-process stub to real distributed work.

---

## 2. The Phase 1 baseline you're extending

Read these BffWeb files first — Phase 2 modifies them in place:

- `src/BffWeb/BffWeb.Api/Controllers/DemoController.cs` — every stub is marked with `// PHASE 2:` describing what real work to wire
- `src/BffWeb/BffWeb.Api/Controllers/SystemController.cs` — currently returns hardcoded health/metrics; Phase 2 wires real probes (see §6)
- `src/BffWeb/BffWeb.Api/SignalR/DemoHub.cs` + `SignalRDemoHubNotifier` — the push surface (no changes needed; just emit more events)
- `src/BffWeb/BffWeb.Api/Demo/DemoStateStore.cs` — keep for in-process patterns that genuinely belong client-side (rate limit fits this)
- `src/BffWeb/BffWeb.Application/Interfaces/IDemoHubNotifier.cs` — wire records the frontend reads

What's already real in Phase 1 and **does not need re-doing**:
- Idempotency (`/api/demo/idempotency/process|key|race`) — uses `DemoStateStore.IdempotencyKeys` with `AddOrUpdate`. Real concurrency demo, in-process is fine.
- Optimistic concurrency (`/api/demo/inventory/{id}` GET/PUT with `If-Match`) — same. *Optional Phase 2:* re-route to catalog-svc's stock entity if you want a true cross-service demo.
- Rate limit (`/api/demo/ratelimit/configure|request|burst`) — `FixedWindowRateLimiter` per session. Honestly client-tier; leave as-is.
- Tracing (`/api/demo/tracing/start` → `DemoTraceStore`) — synthesized 7-span trace. *Optional Phase 2:* wire OpenTelemetry's in-memory exporter to capture real cross-service spans.

---

## 3. Phase 2 work queue (in priority order)

### T2.1 — Honest System probes ✅ DONE

Shipped on `feat/portfolio-bffweb-demos`. New files in BffWeb:
`Application/Interfaces/IDemoActivityCounters.cs`,
`Application/Interfaces/IDependencyHealthProbe.cs`,
`Api/Demo/DemoActivityCounters.cs`,
`Api/Demo/DependencyHealthProbe.cs`,
`Api/Middleware/DemoActivityMiddleware.cs`. SystemController + Program.cs
updated to consume them. Verified live: 7 services probed in parallel
with real per-service latency, identity-svc correctly reported `offline`
during boot then transitioned to `online` after vault-seed gate cleared.
Activity counters went 0→3 with X-Demo-Session-tagged POSTs.

Original brief left below for reference.

---

**Why first:** the portfolio's hero tile + StatusStrip render values from `/api/health/snapshot` and `/api/metrics/snapshot`. BffWeb currently returns hardcoded literals (`p99LatencyMs = 42.4`, `availability = 99.998`, etc). The monolith already replaced these — we ported the fakes in Phase 1 and need to match.

**Source:** `ritualworks` commit `e9d5d88` ("honest metrics + dependency probes"). Files added there:
```
src/Application/Interfaces/IDemoActivityCounters.cs
src/Application/Interfaces/IDependencyHealthProbe.cs
src/Infrastructure/Telemetry/DemoActivityCounters.cs
src/Infrastructure/Telemetry/DependencyHealthProbe.cs
src/Infrastructure/Middleware/DemoActivityMiddleware.cs
```

**Target:** mirror into BffWeb:
```
src/BffWeb/BffWeb.Application/Interfaces/IDemoActivityCounters.cs
src/BffWeb/BffWeb.Application/Interfaces/IDependencyHealthProbe.cs
src/BffWeb/BffWeb.Api/Demo/DemoActivityCounters.cs       (Singleton)
src/BffWeb/BffWeb.Api/Demo/DependencyHealthProbe.cs      (Scoped — uses DbContext)
src/BffWeb/BffWeb.Api/Middleware/DemoActivityMiddleware.cs
```

**Adaptations BffWeb needs:**
- `DependencyHealthProbe` in the monolith uses `CatalogDbContext.Database.CanConnectAsync()`. **BffWeb has no DbContext** — it's a stateless aggregator. Two choices:
  - **A (preferred):** probe each downstream service via the typed `HttpClient`s already wired in `BffWeb.Api/Program.cs` (loop through `BackendClients.*`, hit each `/health` endpoint with a 2s timeout). This is more honest for a microservices BFF anyway — the things BffWeb depends on are *services*, not databases.
  - **B (faster):** drop the postgres probe entirely, just probe Redis + RabbitMQ.
- `DemoActivityMiddleware` needs to be wired in `Program.cs` between `UseRouting()` and `UseAuthentication()`.
- `SystemController` constructor takes `IDemoActivityCounters` + `IDependencyHealthProbe` and reads from them in `GetHealthSnapshot` / `GetMetricsSnapshot` / `GetHealthStream`.

**Acceptance:** call `GET http://localhost:5050/api/health/snapshot` while only Postgres is running (stop Redis with `docker stop redis-...`). The response must show Redis as `degraded` or `down` with non-zero `latencyMs`. Restart Redis; the next snapshot reverts to `online`.

### T2.2 — Saga is real

**Why:** the Checkout demo is the centerpiece. Right now it returns a fake `{ status: "Started" }` immediately. The platform's `CheckoutOrchestrator` MassTransit state machine is fully built and tested (see `tests/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs`).

**What to do:**
1. Add `IPublishEndpoint` to `DemoController` (already available via `BffWeb.Application` MassTransit registration — confirm in `BffWeb.Application/DependencyInjection.cs`).
2. Update `POST /api/demo/saga/start` to publish `CheckoutInitiatedEvent` (look in `src/Contracts/Checkout/`) with the demo scenario header (matches monolith pattern).
3. Update `GET /api/demo/saga/{sessionId}` to call `checkout-orchestrator-svc/api/saga/{sagaId}` via the typed HttpClient `BackendClients.Checkout`.
4. Add a new MT consumer in BffWeb (`src/BffWeb/BffWeb.Api/SignalR/SagaStepConsumer.cs`) that listens for the saga's state-change events (look at the `CheckoutOrchestrator.Domain` events) and calls `IDemoHubNotifier.NotifySagaStepAsync` per state.
5. Register the consumer in `BffWeb.Api/Program.cs`'s existing `AddMassTransit` block.

**Acceptance:** trigger `POST /api/demo/saga/start {scenarioType:"success"}`, watch the SignalR group `demo-{sessionId}` receive `OnSagaStep` events as the saga moves `Pending → AwaitingStock → AwaitingPayment → Completed`. The catalog-svc `Stock` table shows the reservation row appearing then being decremented when paid.

### T2.3 — Circuit breaker against catalog-svc

**Why:** `POST /api/demo/circuit/request {shouldFail:true}` should open a *real* Polly circuit on a *real* HTTP call to catalog-svc, not just flip a static counter.

**What to do:**
1. Add a tiny `[HttpGet("demo/fail")]` endpoint to `Catalog.Api/Controllers/HealthController.cs` (or new `DemoTestController.cs`) that returns `503 ServiceUnavailable`. Annotate `[AllowAnonymous]`.
2. In BffWeb, build a new typed `HttpClient` named `BackendClients.CatalogDemo` with a Polly circuit-breaker policy (2 failures → open for 6s). Use `BuildingBlocks.Resilience.IResiliencePolicyFactory`.
3. Update `POST /api/demo/circuit/request`: when `shouldFail=true` it hits `/demo/fail`; when `false` it hits `/health`. Catch `BrokenCircuitException` → return `circuitState: "open"`. Stream state change to SignalR.

**Acceptance:** call `circuit/request {shouldFail:true}` 3× — first two return `circuitState: closed` with `success: false`; third returns `circuitState: open` with `isRejected: true`. Wait 7 seconds. Call `circuit/request {shouldFail:false}` — circuit half-opens, succeeds, returns to closed.

### T2.4 — Vault rotation drives identity-svc

**Why:** `vault/rotate` currently increments an in-process counter. The platform's identity-svc actually uses Vault credentials at boot.

**What to do:**
1. Add `[HttpPost("admin/vault/rotate-credentials")]` to identity-svc (`Identity.Api/Controllers/AdminController.cs` — new file). Calls into `BuildingBlocks.Vault.IVaultService` to force a credential refresh. `[AllowAnonymous]` for now (locked behind localhost-only middleware in dev).
2. Update BffWeb's `vault/rotate` to call this via `BackendClients.Identity` typed client.
3. Stream the four-stage progression (`started`, `activated`, `grace_period`, `revoked`) — identity-svc can publish those over MT; BffWeb consumer translates to SignalR.

**Acceptance:** `POST /api/demo/vault/rotate` returns 202. Within 1s, identity-svc logs show "VaultService: refreshed AppRole credentials, version=N+1". The frontend shows all four stages in the VaultRotation panel.

### T2.5 — Event flow uses payments outbox

**Why:** `events/trigger` currently fires SignalR events on a `Task.Delay` timer. The platform's payments-svc has a real outbox.

**What to do:**
1. Add `[HttpPost("admin/demo-event")]` to payments-svc that begins a transaction, publishes a `DemoOutboxEvent` (define in `src/Contracts/Payments/`), and commits. The outbox relay drains it.
2. Add a consumer in BffWeb that listens for `DemoOutboxEvent` and emits `OnEventFlow` with `stage: "consumed"`.
3. Update BffWeb's `events/trigger` to call the payments admin endpoint, then immediately stream `stage: "persisted"`.
4. The relay running in payments-svc's hosted service streams `stage: "relayed"` (add a `IDemoHubNotifier` dependency to the relay — or have payments publish a marker event the BffWeb consumer translates).

**Acceptance:** `POST /api/demo/events/trigger` returns immediately. Frontend sees `persisted` → `relayed` → `consumed` events with real timestamps. Payments DB's `OutboxMessages` table shows the row briefly, then it's gone. Pause via `events/relay-pause` — payments-svc's relay should stop draining, queue grows.

### T2.6 — Cache demos use catalog HybridCache

**Why:** stampede + invalidation are real patterns; running them in-process inside BffWeb is theatre.

**What to do:**
1. Verify catalog-svc has `HybridCache` registered (it should via `Catalog.Infrastructure/DependencyInjection.cs`). Add if missing.
2. Add `[HttpPost("demo/stampede")]` to catalog-svc that runs the existing stampede logic against its real `IProductCache`.
3. Update BffWeb's `cache/stampede` to proxy to catalog-svc. Same for product GET/PUT/DELETE.
4. catalog-svc should emit `OnCacheEvent` via the BffWeb hub — easiest route is publishing a `CacheInvalidatedEvent` over MT, BffWeb consumer translates.

**Acceptance:** `POST /api/demo/cache/stampede {concurrentRequests: 50, protectionMode: "singleflight"}` returns `dbQueries: 1` and the response is real (catalog-svc logs show the singleflight pattern). With `protectionMode: "none"` it returns `dbQueries: ~50`.

### T2.7 — Chaos toggles a Vault flag

**Why:** `chaos/trigger` is currently log-only. Make it actually inject failure into a downstream service.

**What to do:**
1. Pick a chaos contract: a Vault KV path `secret/data/chaos` with field `service-kill: bool`. catalog-svc reads it on each request via a tiny middleware (cached for 5s).
2. BffWeb's `chaos/trigger` writes to that KV path via `BuildingBlocks.Vault.IVaultService`. Auto-reverts after `durationSeconds`.
3. catalog-svc's middleware short-circuits with `503` when the flag is set. Trips the circuit from T2.3 if you also click circuit-request during the chaos window.

**Acceptance:** `POST /api/demo/chaos/trigger {scenario: "service-kill", durationSeconds: 10}` then `POST /api/demo/circuit/request {shouldFail: false}` immediately — should still fail (catalog returns 503). After 10s, the next circuit/request succeeds.

---

## 4. Mandatory conventions (read before any code)

These are enforced by the platform's `.claude/rules/` and `Directory.Build.props`. Violation breaks the build (`TreatWarningsAsErrors=true`).

### Code style
- File-scoped namespaces, primary constructors, `internal sealed` for handlers/consumers/validators
- `async` methods end with `Async`. Methods marked `async` MUST have `await` (CS1998 is fatal — Phase 1 hit this; if your method is fire-and-forget, drop `async` and return `Task.FromResult` / synchronous).
- Private fields: `_camelCase`. No magic strings — see `.claude/rules/code-quality.md`.

### DI lifetime — captive dependency
- `IDemoHubNotifier` is **Singleton** in BffWeb. Do not change. Anything that needs to push to SignalR can take it as a constructor dep.
- New services: prefer Singleton for stateless infra, Scoped for things that need a `DbContext` or per-request state.
- ASP.NET Core's `ValidateScopes` runs in Development and crashes on captive deps at boot — Phase 1 hit this with the monolith and we documented the fix in `SignalRDemoHubNotifier`'s XML doc. Read it.

### Wire format pinned
- Response shapes from `DemoController` and `SystemController` MUST stay byte-compatible with `portfolio-site/src/lib/api/demo-client.ts`. Adding a property is safe; renaming or reordering breaks the client.
- SignalR event names + payloads MUST match `portfolio-site/src/lib/api/signalr.ts`.

### CORS already wired
- BffWeb's `Program.cs` has `AddCors("portfolio-site")` allow-listing `http://localhost:4321`. Don't touch unless you're adding a new origin.

---

## 5. Per-task workflow

For each item in §3:

1. **Branch:**
   ```
   git checkout -b feat/bffweb-phase2-<short-name> main
   # e.g. feat/bffweb-phase2-saga-real, feat/bffweb-phase2-honest-system
   ```
2. **Read first:**
   - The corresponding monolith handler (which already does the real work)
   - The platform service you're calling into (does the endpoint exist? do you need to add a `/admin/*` or `/demo/*` endpoint there too?)
   - `BffWeb.Api/Program.cs` — see what's already DI-registered
3. **Implement.**
4. **Build the changed projects:**
   ```
   dotnet build src/BffWeb/BffWeb.Api/BffWeb.Api.csproj
   dotnet build src/<TargetService>/<TargetService>.Api/<TargetService>.Api.csproj
   ```
5. **Run end-to-end:**
   ```
   ./scripts/aspire-up.sh   # if it exists in platform; else: dotnet run --project deploy/aspire
   ```
   Then probe the demo endpoint with curl. The acceptance criteria for each task tell you what to check.
6. **Acceptance** (all must hold per task):
   - [ ] `dotnet build <target>.csproj` is clean (zero warnings under `TreatWarningsAsErrors`).
   - [ ] The demo endpoint's response shape is unchanged (run the same curl that worked in Phase 1, JSON should still parse).
   - [ ] The "real" assertion in §3's acceptance line passes (check the downstream service's logs, DB rows, RabbitMQ messages — whatever makes it real).
   - [ ] The `// PHASE 2:` comment on the affected handler is **removed**.
   - [ ] Portfolio site demo still functions in browser at `http://localhost:4321/`.
7. **Commit per task** using this style:
   ```
   feat(bff-web): T2.X — <demo name> now exercises <service>

   <one paragraph: which stub is replaced, what real call/event chain runs, where to see the real artifact (DB row, queue message, log line)>

   Removed `// PHASE 2:` markers in DemoController:<line>.
   Co-Authored-By: <your model name>
   ```
8. **One PR per task.** PR body must include:
   - Which demo
   - The real downstream service it now touches
   - Curl command that proves the new behavior
   - Anything that needed adding to the downstream service (new admin endpoint, new event type, etc.)

---

## 6. Escalation — when to stop and ask

1. **The downstream service doesn't have the endpoint or event you need.** Don't add it under that service's name unless it makes domain sense. For T2.4 (vault rotate) you SHOULD add an admin endpoint to identity-svc — that's its job. For T2.7 (chaos) you might NOT want to dirty catalog-svc with chaos-aware middleware; reconsider whether to use a tiny dedicated `chaos-svc` instead. Flag in PR.
2. **The wire format would have to change to make a demo real.** Stop. The frontend contract is the constraint. Either find another way or coordinate a frontend change in a sibling PR on portfolio-site.
3. **A change to an existing platform service breaks its tests.** Don't merge. Fix the tests in the same PR, or rethink the change.
4. **You discover that a Phase 2 task needs work in *three* services.** Split into smaller PRs.

---

## 7. Reference: useful greps

```bash
# Find platform code under test by name
grep -rn "class CheckoutInitiatedEvent" /Users/chidionyema/Documents/code/ritualworks-platform/src/

# Find existing typed HttpClient registrations
grep -rn "BackendClients\." /Users/chidionyema/Documents/code/ritualworks-platform/src/BffWeb/

# See how monolith does the real work (for comparison)
grep -rn "DemoController\|DemoHub" /Users/chidionyema/Documents/code/ritualworks/src/ | head -20

# Confirm BffWeb's CORS / DI / hub mapping you're touching
grep -nE "AddSingleton|AddCors|MapHub" /Users/chidionyema/Documents/code/ritualworks-platform/src/BffWeb/BffWeb.Api/Program.cs
```

---

## 8. What "done with Phase 2" looks like

When all of §3.T2.1 through T2.7 are merged:
- BffWeb has zero `// PHASE 2:` comments left
- Stopping any one downstream microservice breaks exactly the demos that genuinely depend on it (and the StatusStrip shows that service as down — proving probes are real)
- Portfolio-site README + ritualworks-platform CASE-STUDY.md updated to drop the "fake/in-process" caveats
- The agent-briefs/portfolio-bffweb-phase2.md (this file) is moved to `docs/agent-briefs/done/` or deleted

After Phase 2 the deliverable matches what the case study says: every interactive demo on the portfolio site is real cross-service traffic running on a 7-microservice .NET 9 platform.

---

**End of brief.** Pick T2.1 to start.
