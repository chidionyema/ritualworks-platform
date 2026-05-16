# E2E Framework — End-to-End Spec

**Status:** spec — not yet implemented.

## 1. Goal & non-goals

### Goal

A single test suite (`tests/E2E/`) that boots the entire Aspire AppHost (all services + real Postgres + Rabbit + Redis + Vault + Identity) and asserts that golden user journeys complete with the right side-effects (DB rows, events, downstream calls). One test = one user-visible outcome. The suite is **the equation** that proves the system works.

The protocol invariant: **if every journey passes, the platform delivers its contracts.**

### Non-goals

- Per-service unit/integration tests — those stay where they are (`tests/<Svc>.Unit/`, `tests/<Svc>.Integration/`).
- Performance / load testing — separate suite (`tests/Perf/` if and when needed).
- UI / visual regression — frontend's responsibility.
- Manual exploratory tests — these are deterministic golden journeys.

## 2. Architecture at a glance

```
tests/E2E/
├── E2E.csproj                            # refs HaworksPlatform.AppHost + every Api csproj
├── Fixtures/
│   ├── AppHostFixture.cs                 # xUnit collection fixture: boot AppHost once per run
│   ├── JwtIssuer.cs                      # mint test JWTs (any role/sub)
│   ├── EventBusObserver.cs               # MassTransit ITestHarness wrapper for assertion
│   ├── DbAssertions.cs                   # query helpers: row counts, status checks per service DB
│   └── HttpClientPool.cs                 # per-service HttpClient with auto-retry on 503 startup
├── Journeys/
│   ├── CustomerCheckoutJourney.cs        # the headline journey — touches ~every service
│   ├── RefundJourney.cs
│   ├── PromotionRedemptionJourney.cs     # cart with code → quote → order → redemption
│   ├── NotificationDeliveryJourney.cs    # event publishes → notification dispatched
│   ├── VaultRotationJourney.cs           # rotate creds, services keep working
│   └── AuditTrailJourney.cs              # any state change → audit row + redaction
└── README.md                             # how to run, fixture lifetime, debugging tips
```

**Substrate:** `Aspire.Hosting.Testing` (already used by `tests/Smoke/`). Boots AppHost in-process, exposes a `DistributedApplication` instance, services run as actual processes/containers. Tear-down is automatic.

**Determinism:**
- Each journey is wrapped in a `[Collection("E2E")]` so they run sequentially, not in parallel (avoids cross-test data contamination).
- Each test seeds its own data via test-only HTTP endpoints or direct DB inserts.
- Time-dependent assertions use `IClock` injected from a test fixture (no `DateTime.UtcNow` directly).
- Random IDs are generated from a per-test seeded `Random`.

## 3. The journey contract

A "journey" is one test method that:

1. **Sets up** — seeds prerequisite data (a customer, a product, a promotion).
2. **Acts** — issues real HTTP calls or publishes real events.
3. **Asserts state at every hop** — not just final response codes. Read DB rows in each affected service, assert event bus saw the right messages, assert downstream services were called.
4. **Cleans up** — implicit via collection fixture teardown.

```csharp
[Collection("E2E")]
public class CustomerCheckoutJourney(AppHostFixture host)
{
    [Fact]
    public async Task Customer_can_search_add_to_cart_apply_promo_pay_and_receive_confirmation()
    {
        // Setup
        var customer = await host.SeedCustomer(role: "customer");
        var product  = await host.SeedProduct(name: "Widget", priceCents: 5000);
        var promo    = await host.SeedPromotion(code: "SUMMER10", percentOff: 10);

        // 1. Customer searches
        var searchResp = await host.Bff.GetAsync($"/api/search?q=Widget");
        searchResp.Should().HaveStatusCode(200);

        // 2. Adds to cart, applies promo
        var quoteResp = await host.Pricing.PostAsync("/price/quote", new {
            cart_lines = new[] { new { product_id = product.Id, qty = 1 } },
            customer_id = customer.Id,
            promo_code = "SUMMER10"
        });
        quoteResp.Should().HaveJson(j => j.GetProperty("final_total_cents").GetInt32() == 4500);

        // 3. Places order
        var orderResp = await host.Checkout.PostAsync("/checkout", new {
            customer_id = customer.Id,
            cart_lines = new[] { new { product_id = product.Id, qty = 1 } },
            quote = quoteResp.Body
        });
        orderResp.Should().HaveStatusCode(201);
        var orderId = orderResp.Json("$.order_id");

        // 4. Pays (test-mode payment provider auto-approves)
        var payResp = await host.Payments.PostAsync($"/payments/{orderId}/confirm");
        payResp.Should().HaveStatusCode(200);

        // 5. Assert state at every hop
        await host.Db.Orders.HasRow(orderId, status: "Paid");
        await host.Db.Pricing.HasRedemption(promo.Id, orderId);
        await host.Db.Audit.HasEventCount(orderId, atLeast: 5);
        await host.EventBus.Saw<OrderCompletedEvent>(e => e.OrderId == orderId);
        await host.EventBus.Saw<NotificationRequestedEvent>(e => e.CustomerId == customer.Id);
        await host.Db.Notifications.HasDelivered(customer.Id, channel: "email");
    }
}
```

## 4. Fixture design

### `AppHostFixture` (collection fixture, lifetime = one per test run)

- Boots the AppHost via `DistributedApplicationTestingBuilder.CreateAsync<Projects.HaworksPlatform_AppHost>()`.
- Waits for every service's `/health` to return 200 (with a 60s timeout).
- Exposes per-service typed HttpClients: `Bff`, `Catalog`, `Pricing`, `Checkout`, `Payments`, `Orders`, `Notifications`, `Audit`.
- Exposes per-service DB query helpers: `Db.Orders`, `Db.Pricing`, `Db.Audit`, etc. (read-only — assertions only, never writes).
- Exposes `EventBus` — a MassTransit `ITestHarness` listening on the same RabbitMQ that the AppHost wires.
- Disposes everything cleanly.

### `JwtIssuer`

- Mints test JWTs signed by the test-mode JWKS Identity is configured to trust.
- Helper: `host.MintJwt(role: "audit-reader")` returns a Bearer token.
- Used by HttpClient pool to authenticate test requests as different personas.

### `EventBusObserver`

Wraps MassTransit's `ITestHarness` so journey tests can assert:
- "Saw a `PaymentCompletedEvent` matching predicate within 5 seconds" (tolerates async dispatch).
- "Saw exactly N events of type X" (count assertion).
- "Saw events in order: A → B → C" (ordering assertion).

### `DbAssertions`

Per-service typed read helpers that connect to the AppHost-managed Postgres. Examples:
- `host.Db.Orders.GetById(orderId)` → returns the row (or fails with helpful message).
- `host.Db.Audit.RowsForEntity(entityId)` → returns matching audit rows.
- All read-only — no inserts (those happen via service APIs).

## 5. SLA targets

| Test | Wall-clock budget |
|---|---|
| `AppHostFixture` startup (once per run) | < 60s |
| Each journey (steady state, AppHost already up) | < 30s |
| Full E2E run (all journeys) | < 5min |

If a journey exceeds 30s, that's a structural defect (per the integration-test memory: slow tests are bugs, not facts of life). Triage at the test level, not the timeout level.

## 6. Test plan

### 6.1 Journeys to implement (priority order)

1. **CustomerCheckoutJourney** — search → quote → checkout → pay → notify → audit. Touches every service. If this passes, ~80% of the platform is provably working.
2. **PromotionRedemptionJourney** — cart with code → quote with discount → order → redemption recorded → single-use promo retired.
3. **RefundJourney** — Paid order → refund request → payment reversed → notification → audit trail.
4. **NotificationDeliveryJourney** — event published → notification consumer → channel gateway → delivery status.
5. **VaultRotationJourney** — rotate Vault creds mid-run → assert all services keep serving (no 5xx).
6. **AuditTrailJourney** — drive specific scenarios, assert audit rows are correct (redaction, idempotency, partition routing).

### 6.2 Done check

`dotnet test tests/E2E/E2E.csproj` exits 0 with all journeys green, in under 5 minutes wall-clock, on the canonical CI runner.

## 7. Topology & runtime

- Local: `dotnet test tests/E2E/E2E.csproj` boots Aspire on the developer's machine. Requires Docker.
- CI: same command in a workflow that has Docker available (existing `ci.yml` runner).
- The AppHost configuration is **shared with production** — same csproj, just different env. Tests prove the same code path users hit.

## 8. Observability

- Each journey logs its phases (1: setup, 2: act, 3: assert) with structured logs.
- Failing journey dumps: AppHost logs (per service), event-bus harness state, and DB row dumps for the affected entities.
- Test runner emits TRX so CI can render per-test results.

## 9. Failure modes & recovery

| Failure | Action |
|---|---|
| AppHost won't boot | Test fixture fails fast with a per-service health summary. |
| Service intermittent 5xx | Treated as failure (no retry budget at the test level — services must be reliable). |
| Event bus message lost | Treated as failure — exposes a real bug, don't hide. |
| Test data collision | Each test seeds with a unique GUID prefix; collisions = test bug. |

## 10. Implementation plan (parallel agents)

After E2E.csproj + the AppHostFixture core ship, journeys can be added in parallel.

| Track | Scope | Hours |
|---|---|---|
| **L0 — Project + AppHostFixture** | E2E.csproj, AppHostFixture.cs, JwtIssuer, HttpClientPool. The shell that boots Aspire and exposes typed clients. | 4 |
| **L1A — DbAssertions + EventBusObserver** | Per-service read helpers + MassTransit test-harness wrapper. | 3 |
| **L1B — CustomerCheckoutJourney** | The headline journey + any seed-helpers it needs. | 4 |
| **L1C — PromotionRedemptionJourney + RefundJourney** | Two more journeys exercising pricing + payments paths. | 4 |
| **L1D — NotificationDeliveryJourney + AuditTrailJourney** | Notification + audit journeys. | 3 |
| **L1E — VaultRotationJourney** | Mid-run rotation; trickier because it touches all services. | 3 |

Total: ~21 hours, ~3-4 calendar hours with 4 agents in parallel after L0.

**Reference services to mirror:** `tests/Smoke/Smoke.csproj` (already uses `Aspire.Hosting.Testing`), `tests/Catalog.Integration/` (per-service integration test shape).
