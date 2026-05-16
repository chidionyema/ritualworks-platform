# Webhooks Service — End-to-End Spec

Outbound webhook delivery to merchants, partners, and external
integrators. Subscribes to internal domain events; signs, retries, and
delivers HTTP POSTs to subscriber-configured URLs; tracks deliveries
and exposes a delivery-receipt API.

Pairs with the cross-cutting roadmap in
`cross-cutting-services-roadmap.md` (Tier 1, build when first external
integrator asks).

## 1. Goal & non-goals

### Goal
Reliable, signed, observable outbound HTTP delivery of internal
domain events to subscribers. "Order completed" → POST to
`https://merchant.example.com/hooks` with HMAC signature, retry on
failure, surface delivery attempts in a UI.

### Non-goals
- **Not** an inbound webhook receiver. Stripe / PayPal / carrier
  webhooks land on their respective domain services (Payments has
  `PaymentWebhookValidatedEvent` already; Shipping when built).
- **Not** an internal eventbus replacement. Internal services keep
  using RabbitMQ. Webhooks is a translator from RabbitMQ → external HTTP.
- **Not** a generic "outbound HTTP" facade. The contract is webhooks:
  signed, retried, delivery-receipt'd. If a service needs a fire-and-forget
  HTTP call (e.g., posting to Slack), it does that itself.
- Not a marketplace-style "developer portal" with API key self-service
  yet — admins create subscriptions on behalf of partners until the
  product team asks for self-service.

## 2. Architecture at a glance

```
   internal events (RabbitMQ)
              |
              v
   +----------------------+
   | webhooks-svc         |
   |                      |
   | 1. consume event     |
   | 2. resolve matching  |
   |    subscriptions     |
   | 3. enqueue dispatch  |
   |    job (Hangfire)    |
   |                      |
   |    Hangfire job:     |
   | 4. sign payload      |
   | 5. POST + retry      |
   | 6. record attempt    |
   +----------+-----------+
              |
   +----------+-----------+
   | Postgres             |
   |   subscriptions      |
   |   deliveries         |
   |   delivery_attempts  |
   +----------+-----------+
              |
              v
        external partner
        https://merchant.example.com/hooks
```

Hangfire (or your equivalent — Quartz, Coravel) gives durable,
retryable jobs without standing up a second persistence layer.
Reuses the service's own Postgres.

## 3. Contracts

### 3.1 HTTP — request/response

All routes mounted under `/webhooks`. JWT-required, role
`webhook-admin` for write, `webhook-reader` for delivery viewing.

#### `POST /webhooks/subscriptions`
Create a subscription.
```json
{
  "partnerId": "partner-uuid",
  "url": "https://merchant.example.com/hooks",
  "events": ["OrderCompleted", "RefundIssued"],
  "secret": "auto-generated if omitted",
  "active": true,
  "description": "Acme Corp orders firehose"
}
```

#### `GET /webhooks/subscriptions/{id}`
Read; redacts secret.

#### `PATCH /webhooks/subscriptions/{id}`
Modify (toggle `active`, replace URL, rotate secret).

#### `DELETE /webhooks/subscriptions/{id}`
Soft-delete; preserves delivery history.

#### `GET /webhooks/deliveries`
Query params: `subscriptionId`, `eventType`, `status` (in
`pending|succeeded|failed|exhausted`), `from`/`to`, cursor pagination.

#### `GET /webhooks/deliveries/{id}/attempts`
Per-attempt detail: HTTP status, response body (capped at 8KB),
duration, retry index, attempt timestamp.

#### `POST /webhooks/deliveries/{id}/replay`
Idempotency: forces a re-delivery of a past event. Used by support
when partner says "we missed an order". Generates a new
`delivery_id`, references original.

### 3.2 Inbound events

Subscribes to a curated subset of `Haworks.Contracts` —
**not** every event (audit captures everything; webhooks is partner-facing
and should be a deliberately small surface):

| Internal event                  | External event name (partner-facing) |
| ------------------------------- | ------------------------------------ |
| `OrderCreatedEvent`             | `order.created`                      |
| `OrderCompletedEvent`           | `order.completed`                    |
| `OrderAbandonedEvent`           | `order.abandoned`                    |
| `PaymentCompletedEvent`         | `payment.completed`                  |
| `RefundIssuedEvent`             | `refund.issued`                      |
| `SubscriptionStartedEvent`      | `subscription.started`               |
| `SubscriptionRenewedEvent`      | `subscription.renewed`               |
| `SubscriptionCancelledEvent`    | `subscription.cancelled`             |

When Shipping ships, add `OrderShipped` / `OrderDelivered`. Internal
operational events (`VaultRotationStageEvent`,
`ProductCacheInvalidatedEvent`) are deliberately excluded.

### 3.3 Outbound — HTTP delivery payload

```json
{
  "event": "order.completed",
  "id": "evt_2026-05-09T18-00-00_abc123",
  "deliveredAt": "2026-05-09T18:00:00Z",
  "deliveryId": "del_xyz",
  "attempt": 1,
  "data": {
    "orderId": "order-1234",
    "customerId": "user-abc",
    "totalCents": 4999,
    "currency": "USD",
    "completedAt": "2026-05-09T18:00:00Z"
  }
}
```

Headers:
- `Webhook-Id: del_xyz`
- `Webhook-Timestamp: 1715277600`
- `Webhook-Signature: t=1715277600,v1=<base64-hmac-sha256>`
- `User-Agent: haworks-webhooks/1.0`
- `Content-Type: application/json`

Signature scheme matches Stripe's (well-documented for partners):
HMAC-SHA256 over `<timestamp>.<request-body>` with the subscription's
`secret`. v1 prefix lets us rotate the algo later.

## 4. Data model — Postgres

```sql
CREATE TABLE webhook_subscriptions (
    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    partner_id      UUID         NOT NULL,
    url             TEXT         NOT NULL,
    secret_hash     TEXT         NOT NULL,           -- bcrypt of plaintext
    secret_preview  TEXT         NOT NULL,           -- last 4 chars for UI
    events          TEXT[]       NOT NULL,           -- ['order.created', ...]
    active          BOOLEAN      NOT NULL DEFAULT true,
    deleted_at      TIMESTAMPTZ,                     -- soft delete
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT now()
);
CREATE INDEX webhook_subs_partner_idx ON webhook_subscriptions (partner_id) WHERE deleted_at IS NULL;
CREATE INDEX webhook_subs_events_gin  ON webhook_subscriptions USING GIN (events) WHERE active AND deleted_at IS NULL;

CREATE TABLE webhook_deliveries (
    id               UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    subscription_id  UUID         NOT NULL REFERENCES webhook_subscriptions(id),
    event_id         TEXT         NOT NULL,         -- the original event message-id
    event_type       TEXT         NOT NULL,         -- 'order.completed'
    payload          JSONB        NOT NULL,         -- the full body that will be delivered
    status           TEXT         NOT NULL,         -- pending|succeeded|failed|exhausted
    next_attempt_at  TIMESTAMPTZ,
    attempts         INT          NOT NULL DEFAULT 0,
    final_status     INT,                            -- HTTP status of last attempt
    created_at       TIMESTAMPTZ  NOT NULL DEFAULT now(),
    completed_at     TIMESTAMPTZ
);
CREATE INDEX webhook_deliveries_status_idx ON webhook_deliveries (status, next_attempt_at) WHERE status IN ('pending','failed');
CREATE INDEX webhook_deliveries_event_idx  ON webhook_deliveries (event_id);
CREATE INDEX webhook_deliveries_sub_idx    ON webhook_deliveries (subscription_id, created_at DESC);

CREATE TABLE webhook_delivery_attempts (
    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    delivery_id     UUID         NOT NULL REFERENCES webhook_deliveries(id) ON DELETE CASCADE,
    attempt_index   INT          NOT NULL,
    started_at      TIMESTAMPTZ  NOT NULL DEFAULT now(),
    duration_ms     INT,
    http_status     INT,
    response_body   TEXT,                            -- capped at 8KB; truncated marker added
    error           TEXT,                            -- for connection-level failures
    succeeded       BOOLEAN      NOT NULL
);
CREATE INDEX webhook_attempts_delivery_idx ON webhook_delivery_attempts (delivery_id, attempt_index);
```

Retention: 90 days for `webhook_delivery_attempts`, 1 year for
`webhook_deliveries` (status snapshot kept as compliance trail).
`webhook_subscriptions` kept forever (soft-deleted).

## 5. Pipeline

### 5.1 Event reception → fan-out

For each consumed internal event:
1. Map internal type → external event name (`OrderCompletedEvent` → `order.completed`).
2. Query active subscriptions whose `events` array contains the external name (GIN index makes this fast).
3. For each subscription, `INSERT` a `webhook_deliveries` row with `status='pending'` and enqueue a Hangfire job keyed on the new `delivery_id`. The insert + enqueue are in the same transaction; Hangfire's job storage IS the same Postgres so it's a single commit.

If no matching subscriptions: short-circuit, no delivery row written.

### 5.2 Dispatch job

Per delivery:
1. Load `webhook_deliveries` + sub.
2. Sign headers.
3. POST with timeout = 10s.
4. Record `webhook_delivery_attempts` row.
5. On 2xx: mark delivery `succeeded`, no more attempts.
6. On non-2xx or transport error: increment `attempts`, schedule retry per backoff schedule.
7. On `attempts >= max_attempts`: mark `exhausted`, fire an internal alert event for support to follow up.

### 5.3 Retry schedule

Exponential with jitter, max 16 attempts over ~3 days:
1m, 2m, 4m, 8m, 16m, 32m, 1h, 2h, 4h, 8h, 16h, 24h, 24h, 24h, 24h, 24h.

Configurable per subscription via `subscription.retry_profile` (`standard`,
`aggressive`, `lenient`). `standard` is the default above.

### 5.4 Idempotency for replays

`POST /webhooks/deliveries/{id}/replay` doesn't mutate the original
delivery — it creates a new one with `payload` copied verbatim and a
fresh `delivery_id`. Partners get a same-event-different-delivery-id
which they should de-dup on `event.id` (which is *also* preserved from
the original). Document this prominently on the partner-facing docs.

### 5.5 Subscription URL validation

On `POST /subscriptions`, send a synchronous test POST with body
`{"event":"webhook.test","data":{}}`. Require 2xx within 5s, otherwise
reject the subscription. Mitigates the "registered a typo URL,
nothing delivers, debug forever" failure mode.

## 6. Other-service changes required

**None on the producer side.** Webhooks consumes existing events.

One small addition recommended: add `eventId` (a stable, unique-per-emit
string) to event metadata if it isn't already in MassTransit's
`MessageId`. Webhooks needs a stable id to dedupe its own output and
to give partners something they can dedupe against. Ideally this is
the same as `MessageId`; verify.

## 7. SLA targets

- **First-attempt latency** (event published → first POST attempt initiated): p95 < 2s, p99 < 10s.
- **Successful-delivery latency** (event published → 2xx received), assuming a fast partner: p95 < 5s, p99 < 30s.
- **Read API p95** (`GET /webhooks/deliveries` filtered to one subscription, last 24h): < 200ms.
- **Loss budget**: zero permanent loss on successful 2xx within 16 attempts. Exhausted = surface, not silent.
- **Availability**: 99.9% (this is partner-visible).

## 8. Topology & deployment

- **Aspire**: `var webhooks = builder.AddProject<Projects.Webhooks_Api>("webhooks-svc")`. References Postgres, RabbitMQ.
- **Compose**: standard backend pattern.
- **Fly.io**: `fly.webhooks.toml`. 2 machines per region for HA — exhausted retries can't pause for a deploy.
- **Sizing**: 256MB RAM, 0.25 vCPU baseline. Hangfire workers + HTTP clients dominate.
- **Outbound HTTP**: needs egress to the public internet — verify on Fly that the service's outbound config isn't restricted.

## 9. Test plan

### 9.1 Unit (`tests/Webhooks.Unit/`)
- `SignatureTests` — golden HMAC fixtures matching Stripe's docs.
- `RetryScheduleTests` — full 16-attempt schedule + jitter bounds.
- `EventTypeMapperTests` — every internal-to-external mapping, error on unmapped event.
- `SubscriptionFilterTests` — GIN-equivalent in-process filter logic.

### 9.2 Integration (`tests/Webhooks.Integration/`)
- `SubscriptionLifecycleTests` — create → URL validation → subscribe → delete.
- `DeliveryHappyPathTests` — publish `OrderCompletedEvent`, assert exactly one POST to a test sink that returns 200, delivery row → `succeeded`.
- `RetryOn5xxTests` — sink returns 503 first 3 attempts, 200 on 4th. Assert 4 `webhook_delivery_attempts` rows + `succeeded`.
- `ExhaustionTests` — sink always 500; assert 16 attempts then `exhausted`.
- `SignatureVerificationTests` — sink verifies signature; assert verification passes for valid secret, fails for tampered body.
- `ReplayTests` — `POST /replay` creates new delivery with same payload, partner-side dedupe-by-event-id holds.
- Use `SharedTestPostgres` + a WireMock-backed sink fixture.

### 9.3 Performance (`tests/Webhooks.Perf/`)
- 1k deliveries/sec sustained for 5 minutes; assert first-attempt-latency p99 < 10s.
- 10k pending deliveries on cold-start; assert drain time < 2 minutes.

### 9.4 Smoke (`tests/Smoke/`)
- `WebhooksSmokeTests` — register a sink subscription against the live stack, run an order checkout, assert the sink received the `order.completed` POST within 30s.

## 10. Observability

- Metric: `webhooks.deliveries.attempted_total{event_type, http_status, attempt_index}`.
- Metric: `webhooks.deliveries.duration_seconds{event_type, http_status}` (histogram).
- Metric: `webhooks.deliveries.exhausted_total{event_type, subscription_id}` (alert when > 0 in 5 minutes).
- Metric: `webhooks.subscriptions.active_count{partner_id}`.
- Trace per delivery, span `webhooks.dispatch.<external_event>`. Include subscription id, attempt index, http status as span tags.
- Dashboard: per-subscription success rate, attempt distribution, exhaustion rate, top failing partners.

## 11. Failure modes & runbook stubs

| Failure                                          | Detection                                   | Mitigation                                                                                                  |
| ------------------------------------------------ | ------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| Partner endpoint flat-out down                   | exhausted-deliveries metric                 | Auto-pause subscription after 100 consecutive exhaustions in 1h; email partner contact; require admin reset. |
| Partner endpoint slow (timeouts)                 | duration p95 > 10s for one subscription      | Same auto-pause threshold; tighter timeout option per-subscription.                                         |
| Hangfire job table contention                    | dispatch-latency p95 climbs                 | Move Hangfire to its own Postgres database (still on the shared cluster).                                   |
| Postgres outage                                  | Hangfire can't enqueue                      | RabbitMQ queue backs up upstream; consumer prefetch caps memory pressure. Drain when DB recovers.           |
| Partner secret leaked                            | partner reports + audit                     | `PATCH /subscriptions/{id}` with new secret; old hash retained for 24h to allow partner-side rotation.      |
| Replay storm (admin replays a year of events)    | dispatch queue depth alert                  | Replay endpoint rate-limited per partner; `>1000 events` requires admin role + reason.                      |

Each row gets `docs/runbooks/webhooks-{slug}.md` once an incident
teaches us the right response.

## 12. Implementation plan (parallel agents)

Six workstreams; L0 on critical path, others parallelizable.

- **L0 — skeleton + DI** (~2h): csproj, Aspire wiring, EF Core context, Hangfire registration on the same Postgres, MassTransit consumer scaffolding. Compiles. Boots in Aspire.
- **L1.A — subscription CRUD + URL validation** (~4h): all `/subscriptions` endpoints, secret hashing (bcrypt), preview-suffix UI logic, synchronous URL test on create. Unit + integration tests.
- **L1.B — event mapping + fan-out** (~3h): internal→external mapping, GIN-indexed subscription resolution, `webhook_deliveries` insert + Hangfire enqueue in one transaction. Integration test: publish event → row written + job scheduled.
- **L1.C — dispatch + signing + retry** (~5h): Hangfire job, HMAC signing, 16-step retry with jitter, attempt logging, exhaustion handling. Unit test for signature; integration for retry.
- **L1.D — read API + replay** (~3h): `GET /deliveries`, `GET /deliveries/{id}/attempts`, `POST /deliveries/{id}/replay`. Cursor pagination. Tests.
- **L2.E — operations** (~3h): auto-pause-on-exhaustion logic, alert event on permanent failure, retention cron, replay rate-limit.
- **L2.F — perf hardening** (~2h): connection pooling, HTTP client tuning, batch dispatch profiling, smoke test in Aspire.

Total: ~22 person-hours, 2–3 calendar days for one focused engineer.
