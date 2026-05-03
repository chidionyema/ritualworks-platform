# 01 — Architecture

## Service Map

Seven services in the new system. Each owns one or more bounded contexts that — in the existing monolith reference — were already physically isolated as separate `DbContext`s.

| # | Service | Responsibility | Bounded contexts owned | Outbound deps |
|---|---|---|---|---|
| 1 | **identity-svc** | AuthN, JWT issuance (RS256/JWKS), refresh tokens, user profile, role seeding, JTI revocation | Identity | None (leaf) |
| 2 | **catalog-svc** | Products, categories, reviews, **stock reservation/release** | Catalog | None (leaf) |
| 3 | **orders-svc** | Order aggregate + lifecycle, guest order info, cart projection | Orders | identity-svc (sync gRPC for user lookup), catalog-svc (event-replicated product snapshots) |
| 4 | **payments-svc** | Payment aggregate, subscriptions, Stripe + PayPal SDKs, webhook ingestion | Payments | None — reacts to events; webhooks ingested via HTTP |
| 5 | **content-svc** | Upload sessions, file metadata, ClamAV scan orchestration, MinIO/S3 access | Content | identity-svc (sync gRPC for user authz) |
| 6 | **checkout-orchestrator-svc** | The checkout saga state machine + compensation logic | Checkout (new — saga state lives here, not in Orders) | None — pure event choreographer |
| 7 | **bff-web** | Public HTTP edge: CSRF, controller mapping, SignalR hubs, gRPC client composition | None (composes others) | All backend services via gRPC |

**Stays as a shared NuGet package, never duplicated:**

- `Haworks.Contracts` — integration event records (`PaymentCompletedEvent`, `OrderCreatedEvent`, `StockReservedEvent`, etc.) and gRPC `.proto`-generated clients. SemVer'd. Additive within a major.
- `Haworks.BuildingBlocks` — `Result<T>`, `Error`, `IDomainEvent`, `AuditableEntity` base, MediatR pipeline behaviors, `IDomainEventPublisher` abstraction, transactional-outbox base wiring, structured-logging conventions, OpenTelemetry registration, Vault `DynamicCredentialsConnectionInterceptor`.
- `Haworks.Testing` — Testcontainers fixtures, Aspire smoke harness adapters, Pact verifier helpers.

---

## Monorepo Layout & Boundary Enforcement

Strict monorepo with **physically enforced** service boundaries. The point is not "all in one folder"; it is "the build fails if any service references another service's code."

### Layout

```
src/
├── Identity/                ← own .csproj, own .sln
│   ├── Identity.Api/
│   ├── Identity.Application/
│   ├── Identity.Domain/
│   ├── Identity.Infrastructure/
│   └── Directory.Build.props   ← blocks ProjectReference to ../Catalog/, etc.
├── Catalog/   ... (same shape)
├── Orders/    ...
├── Payments/  ...
├── Content/   ...
├── CheckoutOrchestrator/  ...
├── BffWeb/    ...
├── Contracts/                  ← THE ONLY thing services may reference
│   ├── Haworks.Contracts.csproj
│   └── (record-only event definitions, .proto files)
└── BuildingBlocks/             ← AND this
    └── Haworks.BuildingBlocks.csproj
```

### Boundary enforcement — three layers

1. **`Directory.Build.props` per service folder** — sets `<NoTargets>true</NoTargets>` on cross-service `ProjectReference` items, fails the build with a custom error message.

2. **NetArchTest in each service's Architecture test project**:
   ```csharp
   [Fact]
   public void Catalog_May_Only_Reference_Contracts_And_BuildingBlocks()
   {
       var result = Types.InAssembly(typeof(CatalogMarker).Assembly)
           .Should()
           .NotHaveDependencyOnAny(
               "Haworks.Identity", "Haworks.Orders", "Haworks.Payments",
               "Haworks.Content", "Haworks.CheckoutOrchestrator", "Haworks.BffWeb")
           .GetResult();
       result.IsSuccessful.Should().BeTrue(
           $"Catalog cannot reference sibling services. Violations: {string.Join(", ", result.FailingTypeNames ?? [])}");
   }
   ```

3. **Custom CI step** — greps every `using Haworks.<OtherService>` across `src/<Service>/`, fails the build. Catches namespace-only references that NetArchTest might miss.

This is the load-bearing trio. Without it, the monorepo is a monolith pretending. See [adr/0001-strict-monorepo.md](./adr/0001-strict-monorepo.md).

---

## Data Strategy

### Database-per-service in a shared cluster

| Option | Verdict |
|---|---|
| Schema-per-service in one DB | **Rejected.** This is today's monolith — connection pools, vacuum, WAL, role grants all shared. Boundary is honor-system. |
| **Database-per-service, shared cluster** | **Chosen.** Hard isolation: separate Postgres user, separate connection string, separate `__EFMigrationsHistory`, no cross-DB queries without `postgres_fdw` (which we don't install). One operational footprint. |
| Separate Postgres instance per service | **Premature.** 7 services × prod/staging/dev × HA replica = ~42 instances. The cluster is not the bottleneck. Trivial to promote one DB out later if a service's IO profile demands it (Catalog with heavy search is the likely first candidate). |

In Aspire today: `builder.AddPostgres("postgres")` with seven `postgres.AddDatabase(...)` calls. In production: one managed cluster (RDS / Aurora / Cloud SQL) with seven databases and seven roles. See [02-platform.md](./02-platform.md#secrets-vault-topology) for Vault dynamic credential rotation.

### Cross-service data access — three patterns, in order of preference

1. **Default: events carry the data the consumer needs.** `OrderCreatedEvent` already carries `TotalAmount`, `Currency`, `CustomerEmail`, `OrderLineItems`. Consumer never calls back to producer for context. Cost: fatter events. Benefit: zero runtime coupling, no cascading failures.

2. **Replicated read model (CQRS materialized view).** When a service needs to *query* foreign data (e.g., Orders rendering "your past orders" needs current product names + thumbnails). Subscribe to `Catalog.ProductUpdated` → build local `catalog_product_snapshots` table in the Orders DB. Eventually consistent, owned by Orders, queryable freely.

3. **Sync API (gRPC).** Only for **freshness-critical authority checks** that cannot tolerate seconds of staleness. Examples: "is this user still permitted to checkout *right now*" → identity-svc; "current price *at this moment* of charge" → catalog-svc. Wrap with circuit breaker + timeout. Fall back to cached snapshot on failure, mark transaction `RequiresReview`.

**Hard prohibition:** no service ever opens a connection to another service's database. No `postgres_fdw`. No "shared read replica." Violations regenerate the monolith. Architecture test enforces.

### The Identity / UserId problem

Every service has `UserId` columns. Naive solution = "JOIN identity.AspNetUsers" → distributed monolith. Three rules:

1. **`UserId` is an opaque foreign key.** No FK constraint across databases. Each service stores `Guid` / `string`, period. Referential integrity enforced by producer (Identity), not consumer.
2. **Embed user-snapshot data in events.** Identity publishes `UserProfileChanged` on create/email-change/role-change. Every consumer maintains a local `user_snapshots` read model.
3. **Auth tokens, not auth lookups.** Services trust the JWT — `sub`, `email`, `roles` are claims. Hot-path validation is pure JWT signature check (against JWKS). Identity is consulted only for token issuance, password reset, profile mutation. Identity remains a leaf.

### Per-service outbox/inbox

The current monolith already wires the MassTransit transactional outbox **per-DbContext** via `BoundedContextConsumerDefinition<T,TDb>` (see `src/Infrastructure/Messaging/Definitions/BoundedContextConsumerDefinitions.cs`). This wiring shape is **deliberately extraction-ready** — it survives the split unchanged. After extraction:

- Each service registers exactly one DbContext.
- Each service's `OutboxMessage`, `OutboxState`, `InboxState` tables move with it into its database.
- The `ConfigureOutboxForContext<TContext>` helper is called once per service with that service's only DbContext.
- Inbox dedupe (`InboxState`) gives every consumer idempotency-by-MessageId for free; business-level idempotency is still a per-handler responsibility.

### EF Migrations

Each service runs only its own context. The pattern lifted from the monolith reference (`MigrateAllAsync` auto-discovering `DbContextOptions` from DI) Just Works in each new service because each registers exactly one DbContext. Schemas use `public` from day 1 — no `HasDefaultSchema("orders")` calls. Per-service `__EFMigrationsHistory` lives in `public`. EF migrations are generated fresh per service; the monolith's migrations are reference for *what columns + indexes were needed*, not files to copy (per [ADR-0009](./adr/0009-monolith-as-reference-not-source.md)).

---

## Communication Patterns

### Sync vs Async — decision matrix

**Default: async events.** Sync only when (a) caller is blocked on a query, (b) no state mutation crossing a boundary, (c) latency budget < 100 ms.

| Cross-service interaction | Mode | Why |
|---|---|---|
| Stock reservation during checkout | **Sync gRPC** (CheckoutOrchestrator → catalog-svc) | Reservation is a precondition for order creation; user is blocked; partial reservation worse than failure. `StockReservedEvent` still fires for downstream subscribers. |
| Order creation → payment session creation | **Async event** | Stripe/PayPal calls are slow and flaky. Outbox + retries handle gateway downtime without blocking the user. Frontend gets URL via SignalR push. |
| Payment webhook → order status update | **Async event** | Webhook handler is HTTP-driven by Stripe/PayPal; orders-svc reacts independently. |
| Saga compensation (release stock, mark order abandoned) | **Async event** | Idempotent; broker DLQ replaces custom recovery queues. |
| User profile lookup for invoice rendering | **Sync gRPC** (payments-svc → identity-svc) | Pure query; latency-sensitive. |
| Product detail for admin UI | **Sync REST** (bff-web → catalog-svc) | Pure query; no side effects; fast. |
| `OrderCompletedEvent` fan-out (email, warehouse, analytics) | **Async event** | Producer must not know subscribers. |

**Codified rule:** *"If the operation can be retried later without the user noticing, it's an event. If the operation is a query the caller is waiting on, it's a sync call. State mutations cross service boundaries via events, never sync."*

### MassTransit topology

**Exchange-per-message-type, queue-per-consumer** (MT default).

```
exchange:  ritualworks.payments.v1.payment-completed   (fanout)
   ├── queue: orders-svc.payment-completed             (OrderCompletedConsumer)
   ├── queue: notifications-svc.payment-completed      (future)
   └── queue: analytics-svc.payment-completed          (future)
```

**Naming convention** — owned by the `Contracts` package, not by services:

```
{org}.{producing-context}.v{major}.{event-name-kebab}
e.g. ritualworks.catalog.v1.stock-reserved
     ritualworks.payments.v1.payment-completed
```

The existing `MassTransit:TopologyPrefix` config + `PrefixedEntityNameFormatter` already supports this — extend with `v{major}` segment.

**Schema evolution rules:**
- **Within `v1`:** additive only (new optional fields). Consumers ignore unknown fields (System.Text.Json default).
- **Breaking change:** new exchange `ritualworks.payments.v2.payment-completed`. Producer dual-publishes to v1 + v2 for ≥6 weeks. Consumers migrate at their own pace. After window, v1 is removed via coordinated PR.
- **Envelope headers** (`MessageId`, `CorrelationId`, `ConversationId`, `SagaId`) are immutable contracts — never repurposed, never renamed. Architecture test enforces.

### Sync API stack

| Category | Pick | Why |
|---|---|---|
| Service → service sync | **gRPC + protobuf** | Strong typing matches record-based events; codegen is the contract; binary framing is fast; bidirectional streaming useful later. |
| Browser/mobile sync | **REST/JSON + OpenAPI** | Existing controllers stay REST; CORS/CSRF stack already there. |
| Edge / API gateway | **YARP** | .NET-native, Kestrel-aligned, Aspire-friendly. Used inside `bff-web` for compositional routing across backend services and as the single ingress point. |
| Service → service auth | **mTLS for transport + JWT for user identity** | mTLS proves *caller service* (cert from Vault PKI). JWT (forwarded `Authorization` header) carries *user identity*. Defence in depth. |
| Service → service authz | **Per-RPC scopes in JWT** | E.g., `payments.read`, `catalog.stock.reserve`. Single ASP.NET Core middleware reused across services. |

### Versioning rules — HTTP/gRPC

- REST: URL versioning `/api/v1/...` → `/api/v2/...`. Both live in parallel for one deprecation window. `Sunset:` header on deprecated version.
- Protobuf: never reuse a field number. Adding fields = additive. Removing = reserve the field number first; full removal at next major.
- OpenAPI diff in CI fails on a breaking change unless PR has `breaking-change-approved` label.

---

## The Saga: CheckoutOrchestrator

This is the crown jewel of the architecture and the highest-risk extraction. It gets its own service.

### Why a separate service

Today, `CheckoutSagaState` is persisted in `OrderDbContext` — a pragmatic monolith decision. Conceptually the saga coordinates Orders **and** Payments **and** Catalog. Pinning it to orders-svc would make Orders a de-facto orchestrator for Payments — exactly the distributed monolith smell.

CheckoutOrchestrator owns:
- `CheckoutDbContext` containing `CheckoutSagaState` + per-context outbox/inbox tables.
- The `CheckoutSaga` state machine (lifted from `src/Infrastructure/Messaging/Sagas/CheckoutSaga.cs`).
- Saga timeout schedules (e.g., 15-minute payment expiry).
- Compensation orchestration: stock release on payment failure, order abandonment on stock failure.

CheckoutOrchestrator owns **no business state** — Orders are still owned by orders-svc, payments by payments-svc. The saga only holds correlation data + last-known status.

### The saga choreography

```
1. bff-web POST /checkout
   ↓
2. orders-svc.CreateOrder
   • creates Order (status=Pending)
   • publishes OrderCreated  (outbox)
   ↓
3. checkout-orchestrator-svc.CheckoutSaga
   • spawns saga instance, correlated by SagaId
   • publishes ReserveStock command
   ↓
4. catalog-svc.StockReservationConsumer
   • reserves stock atomically (WHERE stock >= qty)
   • publishes StockReserved (or StockReservationFailed)  (outbox)
   ↓
5. checkout-orchestrator advances state
   • publishes CreatePaymentSession command
   ↓
6. payments-svc.PaymentSessionConsumer
   • calls Stripe/PayPal
   • publishes PaymentSessionCreated (or PaymentSessionFailed)  (outbox)
   ↓
7. checkout-orchestrator advances state
   • bff-web pushes checkout URL to user via SignalR
   ↓
8. (later) Stripe webhook → payments-svc → publishes PaymentCompleted
   ↓
9. checkout-orchestrator transitions saga to terminal state
   • orders-svc transitions Order to Paid
   • notifications-svc emails receipt
```

### Compensation paths

| Failure point | Compensation |
|---|---|
| `StockReservationFailed` | Saga publishes `OrderAbandoned`. orders-svc marks Order `Abandoned`. notifications-svc emails customer. |
| `PaymentSessionFailed` | Saga publishes `ReleaseStock` + `OrderAbandoned`. catalog-svc releases stock. |
| `PaymentExpired` (15-min timeout fires) | Same as `PaymentSessionFailed`. |
| `PaymentAmountMismatch` (webhook reports wrong amount) | Saga marks Order `RequiresReview`, alerts ops via Slack. |
| `ReleaseStock` itself fails (broker down, catalog-svc down) | MassTransit retry → DLQ → ops alert. Stock zombie possible — verified by chaos test. |

### State queryable for ops

Saga exposes `GetSagaStatus(orderId)` via gRPC. The bff-web `/admin/checkout/{orderId}` endpoint consumes it. This is the kind of "I can see what's stuck" tooling that distinguishes a real distributed system from a Hello World.

### Demo

`make demo-saga-failure` runs a checkout → kills payments-svc pod mid-session → asserts saga compensates (stock released, order abandoned, customer notified) → restarts payments → runs another checkout to green. Recorded as a 2-minute video. *This is the hero shot.*

---

## See also

- [02-platform.md](./02-platform.md) — local dev, K8s deploy, observability, secrets
- [03-build-plan.md](./03-build-plan.md) — phased build order
- [04-testing-strategy.md](./04-testing-strategy.md) — saga chaos tests, contract tests
- [adr/0003-saga-its-own-service.md](./adr/0003-saga-its-own-service.md)
- [adr/0004-database-per-service.md](./adr/0004-database-per-service.md)
