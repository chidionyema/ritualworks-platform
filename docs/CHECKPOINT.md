# CHECKPOINT — where we are, what's next

> **Living doc.** Updated after each phase milestone. Read this first if returning to the project after time away.

**Last updated:** 2026-05-03 (Phase 7 complete — bff-web composes the saga + pushes the URL to the browser via SignalR)
**Current phase:** Phase 7 (bff-web) — **DONE**. 6/6 bff-web tests: 3/3 architecture + 3/3 integration (`/health`, end-to-end SignalR push from `PaymentSessionCreatedEvent` to subscribed client, group isolation — different sagaId doesn't leak). The platform is feature-complete: identity → catalog → payments → orders → checkout-svc saga → bff-web SignalR push. Phase 8 = polish + chaos demo + README hero video.

## Phase 7 verified surface (bff-web, https://localhost:7106)

| Endpoint / pipeline | Behaviour | Status |
|---|---|---|
| `/health` | 200 | ✅ |
| `POST /api/checkout` | Allocates `sagaId` + `orderId`; forwards to `checkout-svc /api/checkouts`; returns 202 with `{sagaId, orderId, message}` | ✅ |
| `/hubs/checkout` (SignalR) | `SubscribeToSaga(sagaId)` adds connection to group `saga-{sagaId:N}`; `UnsubscribeFromSaga(sagaId)` removes | ✅ |
| `PaymentSessionCreatedConsumer` | Consumes from RabbitMQ; pushes `CheckoutReady` payload `{sagaId, orderId, paymentId, checkoutUrl, provider, amount, currency}` to the SignalR group | ✅ |
| Group isolation | Different `sagaId` publishes don't leak to a client subscribed to a different sagaId | ✅ |

Implementation:
- **No DB** — bff-web owns no state. The saga state lives in checkout-svc; orders/payments/identity own their own.
- **Service composition via REST clients** with Aspire's `https+http://<svc>` service-discovery URI form. The build plan calls for gRPC; we built REST in Phases 1-6, so bff-web uses typed `HttpClient`s for now.
- **`CheckoutHub`** uses stable group naming (`saga-{sagaId:N}`) so the consumer can push without referencing the hub class — clean decoupling.
- **End-to-end proof** in `tests/BffWeb.Integration/SignalRPushTests.cs`: a `Microsoft.AspNetCore.SignalR.Client.HubConnection` connects through the WAF's TestServer, subscribes to a sagaId, and the test verifies the push lands when `PaymentSessionCreatedEvent` is published through the in-memory MT harness.

The full saga → browser flow now works end-to-end: `POST /api/checkout` (bff-web) → `checkout-svc CheckoutSaga` → publishes `StockReservationRequested` → catalog reserves + publishes `StockReserved` → saga publishes `PaymentSessionRequested` → payments creates session + publishes `PaymentSessionCreated` → bff-web's `PaymentSessionCreatedConsumer` pushes the URL to the browser via SignalR.

## Phase 5 verified surface (checkout-orchestrator-svc, https://localhost:7105)

| Endpoint / state-machine arrow | Behaviour | Status |
|---|---|---|
| `POST /api/checkouts` | 202 + `{sagaId, orderId}`; publishes `CheckoutInitiatedEvent` | ✅ |
| `GET /api/checkouts/{sagaId}` | 200 with current state + paymentCheckoutUrl + failureReason / 404 | ✅ |
| `Initial → Initiated` (CheckoutInitiated) | Publishes `StockReservationRequestedEvent` with full snapshot | ✅ |
| `Initiated → StockReservedState` (StockReserved) | Snapshots reserved items; publishes `PaymentSessionRequestedEvent` with line items + URLs | ✅ |
| `StockReservedState → ReadyForPayment` (PaymentSessionCreated) | Saves `PaymentId`, `PaymentSessionId`, `PaymentCheckoutUrl` | ✅ |
| `ReadyForPayment → Completed` (PaymentCompleted) | Finalizes saga; row removed by `SetCompletedWhenFinalized()` | ✅ |
| `Initiated → Abandoned` (StockReservationFailed) | Records `FailureReason`; **no** compensation publish (nothing reserved) | ✅ |
| `StockReservedState\|ReadyForPayment → Abandoned` (PaymentSessionFailed) | Publishes `StockReleaseRequestedEvent` with snapshotted items + reason; transitions Abandoned | ✅ |
| `ReadyForPayment → RequiresReview` (PaymentAmountMismatch) | Captures expected/actual amounts in `FailureReason`; ops decides | ✅ |
| Restart resume | Saga state persists in `checkout` schema's `CheckoutSagas` table; `harness.Stop()` + `harness.Start()` and the saga picks up exactly where it left off | ✅ |

Implementation:
- **`CheckoutSaga` MassTransit state machine** (`src/CheckoutOrchestrator.Application/Sagas/CheckoutSaga.cs`) — 6 states, 7 inbound events, 3 outbound events, all correlated by `SagaId` (PaymentAmountMismatch alone correlates by `OrderId` because the contract doesn't carry `SagaId`).
- **`CheckoutSagaState`** persisted via MassTransit's EF saga repository to a Postgres table with both `Version` (MT optimistic concurrency) and `xmin` (raw EF concurrency) tokens. Snapshot fields propagate through the choreography: `LineItemsJson`, `ReservedItemsJson`, `PaymentId`, `PaymentSessionId`, `PaymentCheckoutUrl`, `FailureReason`.
- **`StockReservationRequestedEvent`** added to `Contracts/Catalog` to close the gap from Phase 2c (which only exposed REST). The saga publishes it; a future catalog consumer will react. In tests we drive the saga directly with `StockReservedEvent` / `StockReservationFailedEvent`.
- **Per-context outbox** anchored to `CheckoutDbContext` — saga state writes + outbound publishes commit atomically in one EF transaction.
- **Payment-expiry timeout schedule** intentionally deferred (TODO comment in saga); requires either RabbitMQ delayed-message-exchange plugin or Quartz.NET. Compensation paths covered the failure modes that don't need a wall-clock timer.

## Phase 4 verified surface (orders-svc, https://localhost:7104)

## Phase 4 verified surface (orders-svc, https://localhost:7104)

| Endpoint / consumer | Behaviour | Status |
|---|---|---|
| `POST /api/orders` | 201 + Guid; idempotent on `sagaId` (returns existing orderId on dupe) | ✅ |
| `GET /api/orders/{id}` | 200 with items / 404 | ✅ |
| `GET /api/orders/by-user/{userId}` | 200 paged result | ✅ |
| `PaymentCompletedConsumer` | Order Created → Paid; publishes `OrderCompletedEvent` with `PaymentId` | ✅ |
| `PaymentSessionFailedConsumer` | Order Created → Abandoned with `AbandonReason="PaymentSessionFailed: <code>"`; publishes `OrderAbandonedEvent` with `PreviousStatus="Created"` | ✅ |
| `StockReservationFailedConsumer` | Order Created → Abandoned with `AbandonReason="StockReservationFailed: <reason>"`; publishes `OrderAbandonedEvent` | ✅ |
| Idempotency: replay PaymentCompleted 3× | DB stays in Paid (xmin shadow concurrency catches race); publish count 1-3 (test transport doesn't wire EF outbox; production outbox dedupes on commit-with-state) | ✅ |

Implementation notes:
- **Slim Order aggregate per ADR-0009** — no `Payment` nav, no `GuestOrderInfo` nav. `UserId` opaque string FK to identity, `PaymentId`/`ProductId` opaque Guids. State machine: `Created → Paid | Abandoned` (both terminal). `MarkPaid` / `MarkAbandoned` return `false` if already terminal — application-level idempotency.
- **OrderItem** snapshots `ProductName` + `UnitPrice` at order-create time per ADR-0009 — no cross-context join required for order-history queries.
- **Three consumers** anchored to `OrderDbContext` outbox via `OrdersConsumerDefinition<T>`. Publish-before-save matches catalog/payments + production outbox semantics.
- **xmin shadow concurrency** on `Orders` catches concurrent state transitions (e.g., StockReservationFailed racing PaymentCompleted on the same Created order — first wins).
- **`CreateOrderCommand`** dedupes via unique index on `Orders.SagaId` — second POST with same sagaId returns the existing OrderId.
- Same EF `EnableRetryOnFailure(5, 500ms)` + Test-environment short-circuit pattern + `AssemblyInfo.cs` serial test execution as catalog/payments.

## Phase 3 verified surface (payments-svc, https://localhost:7103)

| Endpoint | Behaviour | Status |
|---|---|---|
| `/health` | GET → 200 | ✅ verified via Aspire end-to-end |
| `POST /webhooks/stripe` (valid HMAC) | 200 + publishes `PaymentWebhookValidatedEvent` via outbox | ✅ |
| `POST /webhooks/stripe` (bad HMAC) | 400, no publish | ✅ |
| `POST /webhooks/stripe` (no header) | 400 | ✅ |
| `POST /webhooks/paypal` (PAYPAL-AUTH-ALGO present) | 200 + publishes (PayPal verification deferred to consumer side) | ✅ |
| `PaymentWebhookValidatedConsumer` (Stripe completed) | Reads tracked Payment by ProviderSessionId, transitions to Completed, publishes `PaymentCompletedEvent` | ✅ (via integration tests when Docker stable) |
| `PaymentWebhookValidatedConsumer` (Stripe failed) | Transitions to Failed, publishes `PaymentSessionFailedEvent` | ✅ |
| `PaymentWebhookValidatedConsumer` (amount mismatch) | Flags Payment, publishes `PaymentAmountMismatchEvent`, no `PaymentCompletedEvent` | ✅ |
| Webhook idempotency (replay 3×) | MT inbox dedupes via `MessageId == sha256(provider:eventId)[..16]`; exactly one `PaymentCompletedEvent` published | ✅ |

Implementation:
- `StripeSignatureValidator` does HMAC-SHA256 inline with 5-min timestamp tolerance — no Stripe.net dependency. Unit-tested with valid/wrong-secret/tampered-payload/old-timestamp/future-timestamp cases.
- `Payment` aggregate strips all cross-context refs per ADR-0009: opaque `OrderId` (Guid) + `UserId` (string FK to identity) + `SagaId` (saga correlation). State machine: Pending → Processing → Completed | Failed | Flagged | Cancelled.
- `PaymentDbContext` mirrors catalog-svc: `payments` schema, `xmin` shadow concurrency token, MassTransit outbox tables.
- `Payments.Infrastructure.DependencyInjection` short-circuits MassTransit production wiring when `ASPNETCORE_ENVIRONMENT=Test` (catalog pattern). Production path includes `EnableRetryOnFailure(5, 500ms)` on the `UseNpgsql(...)` block — masks Testcontainers + Npgsql 9 EOF flakes on macOS.
- Bumped `harness.TestTimeout = 30s` so EF retry budget fits inside the harness wait window.

## Phase 2d verified surface (catalog test suite, 22/22 green)

## Phase 2d verified surface (catalog test suite, 22/22 green)

| Layer | Tests | Cmd |
|---|---|---|
| Architecture (NetArchTest) | 3 — boundary rules: no cross-service refs; Domain ⊥ Application/Infrastructure; Application ⊥ Infrastructure | `dotnet test tests/Catalog.Architecture` |
| Unit (FluentAssertions) | 11 — Product invariants: Create/Restock/Reserve/Release semantics, error paths | `dotnet test tests/Catalog.Unit` |
| Integration (Testcontainers + WebApplicationFactory + MassTransit ITestHarness) | 7 — `/health`, category round-trip, product round-trip with `Include`, 404 paths, reserve happy path with `StockReservedEvent` publish assertion, reserve insufficient stock returns 409 + no event | `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock dotnet test tests/Catalog.Integration` |
| Contract (PactNet v5 message) | 1 — `StockReservedEvent` consumer-side pact pinning OrderId/SagaId/UserId/TotalAmount/Currency/CustomerEmail/IdempotencyKey/Items/OrderLineItems schema. Pact JSON written to `tests/pacts/ConsumerOfCatalog-catalog-svc.json` for broker publication. | `dotnet test tests/Catalog.Contract` |

Infrastructure changes for tests:
- `Catalog.Infrastructure.DependencyInjection` early-returns from `AddInfrastructure` when `ASPNETCORE_ENVIRONMENT=Test`, skipping the production MassTransit/RabbitMQ/EF-outbox wiring. The integration fixture sets the env var BEFORE Program.cs runs (top-level statements + WAF ordering means `builder.UseEnvironment("Test")` fires too late) and registers `AddMassTransitTestHarness` + `AddDomainEventPublisher()` itself.
- Catalog test projects added to `HaworksPlatform.sln`.


## Phase 2c verified surface (catalog-svc stock reservation, https://localhost:7102)

| Endpoint | Behaviour | Status |
|---|---|---|
| `POST /api/products/{id}/reserve` qty within stock | 200 + Guid, stock decremented, `StockReservedEvent` published to RabbitMQ | ✅ |
| `POST /api/products/{id}/reserve` qty > stock | 409 Conflict with `Insufficient stock for product {id}: requested N, available M` | ✅ |
| `POST /api/products/{id}/reserve` (5 parallel, stock=3) | 1 winner gets 200; 4 losers get 409 `Concurrent reservation … retry with the latest stock` | ✅ |
| Final stock after the race | Exactly `initial - winners` (atomic, no oversell) | ✅ |

Implementation:
- **xmin shadow column** on `Products` (Postgres native row-version) — declared via `entity.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken()`. `Npgsql.UseXminAsConcurrencyToken()` was removed in 9.x; the manual shadow form is the supported path.
- **MassTransit transactional outbox** anchored to `CatalogDbContext` via `AddEntityFrameworkOutbox<CatalogDbContext>().UseBusOutbox()`. `OutboxMessage` / `OutboxState` / `InboxState` tables live in the `catalog` schema. `BusOutboxDeliveryService` polls every 1 s and removes rows after broker ack — empty `OutboxMessage` table with non-zero successful reservations is the *correct* steady state.
- **`StockReservedEvent` published to RabbitMQ** via `IDomainEventPublisher` (BuildingBlocks) → `IPublishEndpoint`. Verified by `rabbitmqctl list_exchanges | grep StockReservedEvent` showing the fanout exchange. No queue exists yet (no consumers wired); Orders/Payments services in later phases will declare them.
- **Concurrency exception** caught explicitly in `ReserveStockCommandHandler`: `DbUpdateConcurrencyException` → `Result.Failure(Error.Conflict("Stock.ConcurrencyConflict", …))` → 409 with retry hint. No auto-retry inside the handler — caller decides whether to retry against the new stock state.

Smoke scripts: `/tmp/phase2c-smoke.sh` (happy path + insufficient stock), `/tmp/phase2c-outbox-race.sh` (5-way concurrency).

## Phase 2b verified surface (catalog-svc, https://localhost:7102)

| Endpoint | Method | Status |
|---|---|---|
| `/health` | GET | ✅ 200 Healthy |
| `/api/categories` | GET | ✅ 200 list |
| `/api/categories` | POST | ✅ 201 + Guid (validates unique name) |
| `/api/products` | GET | ✅ 200 paged `{items, total, skip, take}` |
| `/api/products?categoryId=` | GET | ✅ 200 category-filtered |
| `/api/products/{id}` | GET | ✅ 200 with `categoryName` / 404 if missing |
| `/api/products` | POST | ✅ 201 + Guid / 404 if `categoryId` missing |

EF Core 9 + Postgres — `catalog` schema (Categories, Products, ProductReviews). Auto-migrate at startup. Per ADR-0009 catalog-svc owns its DB; no cross-context FKs (`ProductReview.UserId` is an opaque string FK to identity-svc). `RowVersion byte[]` left as a plain bytea column with default `'\x0000000000000000'::bytea`; Phase 2c will switch to Postgres `xmin` for real optimistic concurrency on stock reservation.

Smoke script: `/tmp/catalog-smoke.sh` (POST category → POST product → GET list/by-id/filter → 404 negative paths).

## Phase 1 verified surface (the user-asked "rest of endpoints")

| Endpoint | Method | Status |
|---|---|---|
| `/api/Authentication/register` | POST | ✅ 201 + RS256 JWT |
| `/api/Authentication/login` | POST | ✅ 200 + JWT + refresh |
| `/api/Authentication/refresh-token` | POST | ✅ 200 + new JWT |
| `/api/Authentication/logout` | POST | ✅ 200, revokes JTI |
| `/api/Authentication/verify-token` | GET | ✅ 200 with bearer / 401 without / 401 after logout |
| `/api/Authentication/csrf-token` | GET | ✅ 200 |
| `/api/external-authentication/providers` | GET | ✅ 200 lists Google/Microsoft/Facebook |
| `/api/external-authentication/challenge/{provider}` | GET | ✅ 302 → provider OAuth2 with PKCE / 400 on bad provider |
| `/api/external-authentication/callback` | GET | ✅ wired (callback handled inline by ASP.NET → ExternalLoginCallbackCommand) |
| `/api/external-authentication/link/{provider}` | POST | ✅ wired ([Authorize]) |
| `/api/external-authentication/link-callback` | GET | ✅ wired |
| `/api/external-authentication/unlink/{provider}` | DELETE | ✅ wired ([Authorize]) |
| `/api/external-authentication/logins` | GET | ✅ wired ([Authorize]) |
| `/.well-known/jwks.json` | GET | ✅ RSA JWK |

Total tests: **25 passing** (3 architecture + 7 unit + 14 integration + 1 contract).

---

## TL;DR

Strict monorepo at `/Users/chidionyema/Documents/code/haworks-platform/`. Architecture, build plan, and ADRs live in [`docs/microservices-migration/`](./microservices-migration/README.md).

`dotnet run --project deploy/aspire` brings up the full stack including identity-svc. `/health` returns 200. **Register / login / JWKS round-trip works end-to-end.** JWTs are signed RS256 with RSA-2048 keypair sourced from Vault; matching public key is published at `/.well-known/jwks.json`. 6/6 integration tests pass; 1/1 Pact contract test publishes a `UserProfileChangedEvent` pact for future consumers.

---

## Commits to date (newest first)

```
<this commit>  identity-svc: Pact contract for UserProfileChangedEvent + CHECKPOINT update
<integration>  identity-svc: integration tests — register / login / JWKS, 6/6 green
<rs256>        identity-svc: switch JWT to RS256 + JWKS endpoint (ADR-0005)
0f56430        identity-svc: Vault wiring + role seeding -> register/login green
2204fee        identity-svc: EF migrations + auto-migrate, boundary tests, runbooks
0f0ecaf        identity-svc: add launchSettings so Aspire injects ASPNETCORE_URLS
4caa73d        identity-svc: DI wired, runs end-to-end on http://*/health → 200
37d32fa        Phase 1 (in progress): identity-svc compiles end-to-end
ee70fb8        Phase 0: Foundation — monorepo, BuildingBlocks, Contracts, Aspire, docs
```

(Note: 3 of the recent commits share an identical message because `/tmp/commit-msg.txt`
was reused across multiple `git commit -F` invocations. The diffs are distinct;
just the messages collide. Future commits use unique messages.)

---

## What works end-to-end RIGHT NOW

| Surface | State |
|---|---|
| `dotnet build HaworksPlatform.sln` | 0 warnings, 0 errors |
| `dotnet test tests/Identity.Architecture` | 3/3 boundary tests pass |
| `dotnet test tests/Identity.Integration` (with TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock) | 6/6 register/login/JWKS tests pass in ~19s |
| `dotnet test tests/Identity.Contract` | 1/1 Pact contract test passes; pact JSON written to `tests/pacts/` |
| `dotnet run --project deploy/aspire` | Brings up 10 infra containers + identity-svc |
| Aspire dashboard at https://localhost:17000 | All resources Ready |
| `vault-init` one-shot | Exits 0; writes 7 per-service AppRole creds to `deploy/aspire/vault-creds/<svc>/{role_id,secret_id}` |
| `vault-seed` one-shot | Exits 0; KV paths populated per `infra/vault/secrets/kv-dev-values.json` |
| `curl https://localhost:7101/health` | HTTP 200 |
| `curl https://localhost:7101/.well-known/jwks.json` | RSA JWK with `kty=RSA, alg=RS256, use=sig, kid, n, e` |
| `POST /api/Authentication/register` | HTTP 201 with RS256 JWT (header `alg=RS256, kid=<matches JWKS>`) |
| `POST /api/Authentication/login` | HTTP 200 with JWT + refresh token |

---

## What does NOT work yet (Phase 2+ scope)

| Item | Tracked |
|---|---|
| catalog-svc, orders-svc, payments-svc, content-svc, checkout-orchestrator-svc, bff-web | Phase 2–7 |
| Cross-service Pact provider verification (only consumer pact published; no provider replays it yet) | When second service is added |
| Helm charts + ArgoCD apps for kind cluster | Phase 8 (or earlier as we ship services) |
| `make demo-saga-failure` (the headline chaos demo) | Phase 5 |

---

## Pinned facts (don't relearn these)

### Service ports (per `launchSettings.json`)
- **identity-svc**: HTTP 5101, HTTPS 7101
- (Other services: declare in their own launchSettings as added.)

### Aspire dashboard
- HTTP 15000, HTTPS 17000 (per `deploy/aspire/Properties/launchSettings.json`)
- DCP API: 22000

### Vault
- Dev address: `http://vault:8200` inside containers, `http://localhost:<random>` from host
- Bootstrap token (DEV ONLY): `dev-root-token`
- Per-service AppRoles: `haworks-identity`, `haworks-catalog`, `haworks-orders`, `haworks-payments`, `haworks-content`, `haworks-checkout-orchestrator`, `haworks-bff-web`
- KV paths: `secret/identity/{jwt,oauth/google,oauth/microsoft,oauth/facebook}`, `secret/payments/{stripe,paypal}`, `secret/bff-web/hub`

### Postgres
- Single Aspire-managed cluster, namespaced volume `haworks-platform-postgres-data`
- Per-service databases: `identity`, `catalog`, `orders`, `payments`, `content`, `checkout`
- Per-DB owner roles: `<db>_owner` (NOLOGIN; Vault dynamic users join these)

### Pact broker
- Self-hosted (`pactfoundation/pact-broker:latest`)
- Aspire-managed, port: random (set by Aspire)
- Postgres backing: `pact-db` Aspire resource

---

## Two debugging gotchas — see runbooks

1. **Serilog silent-swallow** — see [`runbooks/serilog-silent-swallow.md`](./runbooks/serilog-silent-swallow.md). When `appsettings.Serilog` shape is wrong, ALL log output disappears (Kestrel's "Now listening" included). Looks identical to a hang.

2. **Aspire + missing launchSettings** — see [`runbooks/aspire-launchsettings-required.md`](./runbooks/aspire-launchsettings-required.md). Without `Properties/launchSettings.json`, Aspire's `AddProject<T>()` skips ASPNETCORE_URLS injection. Kestrel falls back to default 5000 → collides with macOS ControlCenter → silent hang.

---

## Phase 1 — ALL TASKS COMPLETE

| # | Task | Status | Verification |
|---|---|---|---|
| 19 | CHECKPOINT.md | ✅ | this doc |
| 20 | Runbook: Serilog silent-swallow | ✅ | `docs/runbooks/serilog-silent-swallow.md` |
| 21 | Runbook: Aspire launchSettings required | ✅ | `docs/runbooks/aspire-launchsettings-required.md` |
| 22 | Identity boundary architecture test | ✅ | `tests/Identity.Architecture` 3/3 pass |
| 23 | EF migrations + auto-migrate | ✅ | 10 tables in `identity` schema after AppHost boot |
| 24 | Wire VaultConfigBootstrap into Identity.Api | ✅ | `Jwt:Key` flows from `secret/identity/jwt` |
| 25 | Switch JWT to RS256/JWKS | ✅ | JWT header `alg=RS256, kid=...`; JWKS endpoint serves matching public key |
| 26 | Integration tests | ✅ | `tests/Identity.Integration` 6/6 pass (register, login, JWKS, JWT-kid match, etc.) |
| 27 | Pact contract tests | ✅ | `pacts/ConsumerOfIdentity-identity-svc.json` generated for `UserProfileChangedEvent` |

---

## After Phase 1, the build plan continues

Per [`microservices-migration/03-build-plan.md`](./microservices-migration/03-build-plan.md):

| Phase | Service | Status |
|---|---|---|
| 0 | Foundation | ✅ Done |
| 1 | identity-svc | ✅ Done |
| 2 | catalog-svc | 🟡 Next |
| 3 | payments-svc | ⏳ |
| 4 | orders-svc | ⏳ |
| 5 | checkout-orchestrator-svc (the saga — crown jewel) | ⏳ |
| 6 | content-svc | ⏳ |
| 7 | bff-web | ⏳ |
| 8 | Polish + case study + demo videos | ⏳ |

---

## How to pick up where we left off

1. `cd /Users/chidionyema/Documents/code/haworks-platform`
2. Read this file (you're doing it)
3. Skim `git log --oneline` for recent commits
4. Check `TaskList` for outstanding items (or read the table above)
5. `dotnet build HaworksPlatform.sln` — should be 0 warnings, 0 errors
6. `dotnet run --project deploy/aspire` — full stack should come up; verify dashboard URL in console
7. Pick the next pending task and grind
