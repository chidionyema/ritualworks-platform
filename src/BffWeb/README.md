# BffWeb Service

## Overview

BffWeb is the Backend for Frontend (BFF) for the RitualWorks platform. It serves as the single entry point for the portfolio site (`https://ritualworks.pages.dev`) and aggregates calls to downstream microservices. It does not own any persistent domain state.

Key responsibilities:

- **API aggregation and proxying** — routes browser requests to the appropriate downstream service (Identity, Catalog, Orders, Payments, CheckoutOrchestrator, Search, Location, Webhooks, Payouts, Scheduler, Privacy, Merchant, Notifications, Content, Audit).
- **Checkout initiation** — allocates saga and order IDs, derives a user-scoped idempotency key, and forwards to the CheckoutOrchestrator.
- **Real-time push (SignalR)** — three hubs: `CheckoutHub` for saga state updates, `DemoHub` for portfolio demo events, `LiveConsoleHub` for the activity dock.
- **CDC cache invalidation** — consumes Debezium CDC events from the `db.catalog.public.products` Kafka topic and evicts affected entries from the distributed cache.
- **Portfolio demo surface** — `DemoController` drives live demonstrations of idempotency, optimistic concurrency, rate limiting, circuit breaking, saga choreography, and chaos engineering.
- **Observability endpoints** — Server-Sent Event streams for health snapshots, metrics, and topology edges; system identity endpoint.

---

## Architecture

### Layers

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `BffWeb.Domain` | Marker assembly; no domain entities (BFF owns no persistent state) |
| Application | `BffWeb.Application` | `BffCdcCacheInvalidator` (Kafka background service), MassTransit consumer interfaces, `IDemoActivityCounters`, `IDemoHubNotifier`, `IDependencyHealthProbe` |
| Infrastructure | `BffWeb.Infrastructure` | `DomainEventPublisher` wiring; MassTransit registration is intentionally in the API layer (ADR-0010) |
| API | `BffWeb.Api` | Controllers, SignalR hubs, middleware, demo subsystem, typed HttpClient registrations |

### Key Dependencies

- **MassTransit 8.x + RabbitMQ** — consumes saga state events for SignalR fan-out; consumers: `PaymentSessionCreatedConsumer`, `StockReservedSagaBridge`, `StockReservationFailedSagaBridge`, `PaymentSessionCreatedSagaBridge`, `PaymentSessionFailedSagaBridge`, `PaymentCompletedSagaBridge`, `PaymentAmountMismatchSagaBridge`, `VaultRotationStageBridge`, `DemoOutboxEventConsumer`, `ProductCacheInvalidatedBridge`
- **Kafka** — `BffCdcCacheInvalidator` background service (group ID: `bff-web-cdc`) for cache invalidation
- **SignalR** — three hubs for real-time browser push
- **Typed HttpClients** — one named `HttpClient` per downstream service, resolved via Aspire service discovery (`https+http://<svc>`), 4 s timeout
- **Polly** — circuit breaker tuned for demo traffic (70% failure ratio, minimum 10 requests, 15 s sampling, 3 s break)
- **Serilog** — structured logging

### Middleware Pipeline (in order)

1. `UseInstanceIdHeader` — stamps `X-Instance-Id` on every response
2. Swagger (Development only)
3. HTTPS redirect (non-Development only)
4. `UseCors("portfolio-site")` — must precede auth so OPTIONS preflights are answered
5. `UseDemoActivityCounters` — records request counts and latency for `/api/demo/*`
6. `UseLiveConsole` — emits a structured event per `/api/*` request to the live console ring buffer
7. `UseAuthentication` / `UseAuthorization`
8. `MapControllers`
9. `MapHub<CheckoutHub>("/hubs/checkout")`
10. `MapHub<DemoHub>("/hubs/demo")`
11. `MapHub<LiveConsoleHub>("/hubs/console")`

### Delegating Handler Chain (per typed HttpClient)

1. `ChaosFaultInjectionHandler` — short-circuits to synthetic 503 when the target service is "paused" via the chaos manager
2. `UserIdentityForwardingHandler` — propagates `X-User-Id` from the incoming JWT to downstream requests
3. `UpstreamInstanceCaptureHandler` — appends the upstream replica's `X-Instance-Id` to the live console hop list

---

## Domain Model

BffWeb owns no persistent domain entities. The Application layer defines interfaces consumed by the demo subsystem:

| Interface | Purpose |
|---|---|
| `IDemoActivityCounters` | Tracks rolling 24 h ingress event count, active sessions, and p99 latency from `DemoActivityMiddleware` |
| `IDemoHubNotifier` | Sends typed SignalR messages to `DemoHub` clients |
| `IDependencyHealthProbe` | Probes each downstream service + RabbitMQ in parallel with a 2 s timeout |

### Background Services

| Service | Lifetime | Purpose |
|---|---|---|
| `BffCdcCacheInvalidator` | `BackgroundService` | Kafka consumer; evicts `product_detail_{id}` cache keys on CDC changes |
| `JourneyScheduler` | `BackgroundService` | Fires a canonical checkout journey through the cluster every ~20 s (always on, including production) |
| `UpstreamWarmup` | `BackgroundService` | Fires `GET /health` against every backend once on startup to eliminate cold-start circuit breaker trips |

### Demo Subsystem (Singleton services)

| Class | Purpose |
|---|---|
| `DemoStateStore` | In-process state for running demo sessions |
| `DemoActivityCounters` | Rolling histogram for hero metrics |
| `DependencyHealthProbe` | Parallel health probe for all downstream services |
| `LiveConsoleBroadcaster` | Ring buffer + SignalR fan-out for the activity dock |
| `ChaosManager` | (Development only) pauses OS processes or Docker containers for topology-map chaos demos |

---

## API Endpoints

### Checkout

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/checkout` | None | Allocates saga/order IDs, derives idempotency key, forwards to CheckoutOrchestrator. Returns `202 Accepted` with `{sagaId, orderId}` and instructions to connect to `/hubs/checkout`. |

### Locations (proxy)

| Method | Route | Auth | Description |
|---|---|---|---|
| `POST` | `/api/locations` | None | Proxy to `location-svc POST /api/addresses`. |

### System and Observability

| Method | Route | Auth | Description |
|---|---|---|---|
| `GET` | `/api/health/snapshot` | None | Returns a JSON health snapshot: per-service status, latency, p99, timestamp. |
| `GET` | `/api/health/stream` | None | SSE stream; emits a health snapshot every 5 s. |
| `GET` | `/api/metrics/snapshot` | None | Returns `{ingressEvents24h, p99LatencyMs, activeSessions, timestamp}`. |
| `GET` | `/api/metrics/stream` | None | SSE stream; emits a metrics snapshot every 5 s. |
| `GET` | `/api/topology/stream` | None | SSE stream; emits `{id, type, edge}` topology edge events every 1 s. |
| `GET` | `/api/system/identity` | None | Returns `{service, instanceId, gitSha, processStartedAt}`. |

### Demo Endpoints (all `[AllowAnonymous]`)

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/demo/saga/start` | Starts a real checkout saga via CheckoutOrchestrator. Resolves a demo product from Catalog first. |
| `GET` | `/api/demo/saga/{id}` | Polls saga state from CheckoutOrchestrator. |
| `POST` | `/api/demo/idempotency` | Demonstrates idempotent request deduplication. |
| `POST` | `/api/demo/concurrency` | Demonstrates optimistic concurrency control (ETag / If-Match). |
| `POST` | `/api/demo/ratelimit` | Demonstrates rate limiting. |
| `GET/PUT/DELETE` | `/api/demo/cache/product/{id}` | Cache read/invalidation demo. |
| `POST` | `/api/demo/circuit/break` | Circuit breaker demo using a dedicated Catalog HttpClient with Polly. |

### Chaos (Development only)

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/chaos/pause/{service}` | Pause a service process or container. |
| `POST` | `/api/chaos/resume/{service}` | Resume a paused service. |

### Reservations and Search (proxy)

| Method | Route | Description |
|---|---|---|
| `*` | `/api/reservations/*` | Proxy to the Catalog reservation endpoints. |
| `*` | `/api/search/*` | Proxy to the Search service. |

### Subscriptions (proxy)

| Method | Route | Description |
|---|---|---|
| `*` | `/api/subscriptions/*` | Proxy to the Webhooks subscription endpoints. |

### SignalR Hubs

| Hub | Path | Purpose |
|---|---|---|
| `CheckoutHub` | `/hubs/checkout` | Pushes saga step updates to browsers subscribed by saga ID |
| `DemoHub` | `/hubs/demo` | Pushes demo stage events (saga steps, cache events, outbox events) |
| `LiveConsoleHub` | `/hubs/console` | Broadcasts live HTTP request events from the activity dock |

---

## Events

### Consumed via RabbitMQ (MassTransit)

| Consumer | Event | Action |
|---|---|---|
| `PaymentSessionCreatedConsumer` | `PaymentSessionCreatedEvent` | Pushes payment checkout URL to browser via `CheckoutHub` |
| `StockReservedSagaBridge` | `StockReservedEvent` | Pushes saga step `stock_reserved` to `DemoHub` |
| `StockReservationFailedSagaBridge` | `StockReservationFailedEvent` | Pushes saga step `stock_reservation_failed` to `DemoHub` |
| `PaymentSessionCreatedSagaBridge` | `PaymentSessionCreatedEvent` | Pushes saga step `payment_session_created` to `DemoHub` |
| `PaymentSessionFailedSagaBridge` | `PaymentSessionFailedEvent` | Pushes saga step `payment_session_failed` to `DemoHub` |
| `PaymentCompletedSagaBridge` | `PaymentCompletedEvent` | Pushes saga step `payment_completed` to `DemoHub` |
| `PaymentAmountMismatchSagaBridge` | `PaymentAmountMismatchEvent` | Pushes saga step `amount_mismatch` to `DemoHub` |
| `VaultRotationStageBridge` | Vault rotation event | Pushes Vault rotation stage to `DemoHub` |
| `DemoOutboxEventConsumer` | `DemoOutboxEvent` | Pushes `stage=consumed` to `DemoHub` (event-flow demo) |
| `ProductCacheInvalidatedBridge` | `ProductCacheInvalidatedEvent` | Pushes cache invalidation event to `DemoHub` |

### Consumed via Kafka CDC

| Topic | Consumer | Action |
|---|---|---|
| `db.catalog.public.products` | `BffCdcCacheInvalidator` | Evicts `product_detail_{id}` from the distributed cache |

### Published

BffWeb does not publish domain events. It forwards requests to downstream services and bridges events to SignalR.

---

## Configuration

| Key | Source | Description |
|---|---|---|
| `ConnectionStrings:rabbitmq` | Aspire `WithReference(rabbitmq)` | RabbitMQ AMQP URI (required in non-Test environments) |
| `ConnectionStrings:kafka` | Aspire `WithReference(kafka)` | Kafka bootstrap servers for CDC consumer |
| `Cors:AllowedOrigins` | App config / Fly secret | Array of allowed CORS origins. Defaults: `http://localhost:4321`, `https://ritualworks.pages.dev`, `https://portfolio-showcase.pages.dev` |

Service discovery URIs are resolved by Aspire (`https+http://<service-name>`). No static base addresses are configured for downstream service clients.

The Kafka consumer and `BffCdcCacheInvalidator` are disabled in the `Test` environment. MassTransit consumers are also disabled in the `Test` environment.

---

## Database

BffWeb has no database of its own. It uses a distributed cache (injected via `IDistributedCache`) for product detail caching. The cache backend is configured by the Aspire host.

---

## Testing

Test projects live under `tests/BffWeb/`.

| Project | Type | Coverage |
|---|---|---|
| `BffWeb.Unit` | Unit | Application-layer interface implementations |
| `BffWeb.Integration` | Integration | Real HTTP flows through the BFF, SignalR push verification, identity forwarding |
| `BffWeb.Architecture` | Architecture | NetArchTest layer boundary enforcement |

### Integration Test Files

| File | What it tests |
|---|---|
| `SignalRPushTests.cs` | Verifies that saga bridge consumers trigger SignalR messages to connected clients |
| `UserIdentityForwardingTests.cs` | Verifies that `X-User-Id` is correctly propagated from the incoming JWT to downstream requests |

Integration tests use `BffWebFactory` with the `Test` environment, which suppresses the Kafka consumer, `JourneyScheduler`, `UpstreamWarmup`, and MassTransit.
