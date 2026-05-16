# Multi-instance services in Aspire — guide and gotchas

This doc is the operator guide for replicating .NET services in the
`haworks-platform` Aspire AppHost. It exists because most of the
"this is a distributed system" claim hinges on services actually
running in N>1 instances, and the difference between "I added
`WithReplicas(3)`" and "the demo proves distributed behaviour" is
non-trivial.

## What `WithReplicas(N)` does

In `deploy/aspire/Program.cs`:

```csharp
var catalog = builder.AddProject<Projects.Catalog_Api>("catalog-svc")
    .WithReplicas(3)                    // <-- 3 copies
    .WaitFor(catalogDb)
    .WithReference(catalogDb)
    .WithReference(rabbitmq);
```

At AppHost startup, DCP (Aspire's process supervisor) spawns N copies
of the service binary. Each copy gets:

- Its own dynamically-allocated TCP port for each named endpoint
- Its own console output stream visible in the Aspire dashboard
  (`https://localhost:17000`) under separate rows
- An independent process lifecycle — kill one, the others continue

In front of the replicas sits Aspire's reverse proxy (`dcpctrl`). When
a consumer resolves the service via service-discovery (e.g. BFF calling
`https+http://catalog-svc`), the proxy round-robins requests across the
N replicas. From the consumer's perspective, nothing changes — same URL,
load-balancing is automatic.

## What gets shared automatically

Inherent to Aspire's wiring:

- **Connection strings** — every replica gets the same Postgres / Redis
  / RabbitMQ / Vault env vars. They all connect to the same backing
  services. Postgres sees N clients hitting the same DB; RabbitMQ sees
  N consumers competing on the same queue.
- **Service discovery** — every replica registers under the same logical
  name. Consumers see one service.
- **Configuration** (`appsettings.*.json`, env vars from `WithEnvironment`)
  — identical across replicas.
- **OTel resource attributes** — Aspire injects `service.name` and
  `service.instance.id` automatically; spans from different replicas
  carry distinct instance IDs in Tempo.

## What does NOT get shared (the gotchas)

These are per-replica unless you explicitly distribute them. Most of
the "I added replicas and now the demo lies" failure modes live here.

### 1. In-memory state — the biggest gotcha

Every `static` field, every `IMemoryCache`, every singleton-registered
`ConcurrentDictionary` is **per-replica**. There's nothing automatic
about it.

Examples in the current codebase that break under replication:

- `DemoController.s_circuit` (the static `AsyncCircuitBreakerPolicy`).
  3 BFF replicas = 3 independent circuit breakers, none aware of each
  other's failures. The "circuit tripped" demo only reflects what the
  serving replica saw.
- `DemoController.s_circuitFailure` / `s_circuitSuccess` /
  `s_circuitRejected` counters. Per-replica.
- `DemoStateStore` (singleton with internal dictionaries for rate
  limiters, idempotency keys, demo sessions). Per-replica. Same
  request to two different replicas produces inconsistent state.
- Any L1 cache (e.g. `IMemoryCache` in catalog) holding product data.
  Per-replica. Update on instance A, instance B keeps stale until its
  own TTL expires (unless invalidation pubsub fans out — see #4).

Fix patterns:

- **Move state to Redis** — token buckets, idempotency keys, demo
  session metadata. Redis is already in the stack. Use
  `IDistributedCache` for typed access or raw `StackExchange.Redis`
  for atomic operations like `SET NX EX` for idempotency.
- **Move state to Postgres** — saga state already lives there via MT's
  EF saga repo, which handles competing consumers cleanly.
- **Make state per-session, not per-replica** — for demo state that's
  intrinsically per-user, key by session ID; the state still lives in
  Redis but each session gets one bucket regardless of which replica
  serves it.

### 2. Database migrations on startup

If every replica runs `EnsureCreated()` or `Database.Migrate()` on
startup, you get a thundering herd of migration attempts on cold start.
EF's locking handles correctness but the noise is high and migrations
that aren't idempotent can fail spuriously.

Fix: extract migrations into a one-shot container resource:

```csharp
var migrator = builder.AddProject<Projects.Migrator>("migrator")
    .WaitForCompletion(postgres)
    .WithReference(postgres);

var catalog = builder.AddProject<Projects.Catalog_Api>("catalog-svc")
    .WithReplicas(3)
    .WaitForCompletion(migrator)        // <-- replicas wait for migrations
    .WithReference(catalogDb);
```

If the candidate isn't using a migrator project today, lower-effort
workaround: make migration idempotent (no `EnsureCreated`, only
`Database.Migrate()` which is) and accept the noise. Don't fight it.

### 3. Background services / hosted services

`IHostedService` runs in **every replica**. For polling jobs that pull
work from a queue or table (e.g. MT's outbox delivery service, the
Stripe reconciliation job), this is fine — they compete for the same
work via row-level locks or queue consumer semantics.

For singleton work (cron jobs that should run once per cluster, leader
election, scheduled cleanup), running N copies is incorrect and may
produce duplicate work.

Fix: explicit leader election. Options:

- **Postgres advisory lock** — a hosted service tries to acquire
  `pg_advisory_lock(<key>)` on startup; only one replica gets it; the
  others sleep and retry. Cheap, no extra infra.
- **Redis-based leader election** — `SET key replica-id NX EX 30` with
  periodic refresh. The holder is the leader.
- **MT's job consumer** — for scheduled jobs delivered through the bus,
  MT handles competing-consumer semantics natively.

### 4. Cache invalidation across replicas

Invalidating an L1 cache entry on instance A doesn't touch instance B's
L1. The visitor opens tab A on replica 1, updates a product, opens
tab B on replica 2 — sees stale data until B's TTL expires.

Fix: pubsub fanout. The current codebase already has Redis pubsub for
product cache invalidation
(`ProductCacheInvalidatedBridge` / `ProductCacheInvalidatedEvent`).
Verify the channel subscription fires on every replica's startup;
verify the handler invalidates THIS replica's L1 on every received
message regardless of source.

### 5. MassTransit saga endpoints

When you replicate a service that hosts saga state machines, all
replicas register the same `checkout-saga-state` endpoint. RabbitMQ
delivers each saga's events to one replica at a time (competing
consumers). The saga's persistent state is in Postgres, so all replicas
see the same instance.

Concurrency mode matters:

- **`Optimistic`** (`xmin` shadow concurrency) — correct default for
  most sagas. EF's optimistic concurrency catches concurrent updates;
  MT retries automatically.
- **`Pessimistic`** — adds row-level locks. Use only if optimistic is
  producing too many retries under contention. Costs latency.

The candidate's existing `CheckoutSaga` should already work under
replication if the EF saga repo is set up correctly. Verify by running
the saga storm (Item 2 in the hiring plan) with checkout-svc replicated.

### 6. Static endpoint port pinning

Today's AppHost pins ports for portfolio-site compatibility:

```csharp
var bffWeb = builder.AddProject<Projects.BffWeb_Api>("bff-web")
    .WithEndpoint("http",  e => e.Port = 5050)
    .WithEndpoint("https", e => e.Port = 5051)
```

Under `WithReplicas(N)`, only ONE replica can bind a fixed port. The
other N–1 fail to start.

Fix: the proxy port (`:5050`) is the inbound entry point. Replicas
bind dynamic ports for their actual listeners. Aspire handles this if
you remove `e.Port` from the endpoint mutation:

```csharp
var bffWeb = builder.AddProject<Projects.BffWeb_Api>("bff-web")
    .WithReplicas(2)
    // .WithEndpoint("http", e => e.Port = 5050)  ← remove
    .WithExternalHttpEndpoints();
```

But the portfolio-site frontend hardcodes `http://localhost:5050` in
`.env.local`, which is the proxy port, not a replica's port. So the
proxy port itself stays at 5050 (Aspire's reverse proxy listens there
and forwards to dynamic backend replicas). Verify this in the dashboard
once replicas are running.

### 7. Logging and instance identification

Without explicit work, logs from N replicas blur into one stream when
aggregated. Add the instance ID to every log line:

```csharp
builder.Services.Configure<JsonFormatterOptions>(o =>
{
    // Aspire injects this; surface it in structured logs
    o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.AddLogging(b =>
{
    b.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
    });
});

// Then in Program.cs add the instance ID as a log scope:
var instanceId = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES")
    ?.Split(',').FirstOrDefault(a => a.StartsWith("service.instance.id="))
    ?.Substring("service.instance.id=".Length)
    ?? Guid.NewGuid().ToString("N").Substring(0, 8);
```

Or simpler: rely on OTel's `service.instance.id` resource attribute and
filter logs in the dashboard by replica.

## The 5 specific fixes the current codebase needs for replication

If the candidate runs `WithReplicas(2)` on bff-web today, these are the
things that break:

| # | Where | What breaks | Fix |
|---|---|---|---|
| 1 | `DemoController.s_circuit` (static Polly breaker) | Each replica has its own breaker; demo's "tripped" state isn't shared | Move state to Redis, OR keep per-replica and document as "per-instance breaker" (defensible) |
| 2 | `DemoController.s_circuitFailure/Success/Rejected` (static counters) | Each replica counts only its own requests; demo numbers wrong | Move counters to Redis with `INCR` |
| 3 | `DemoStateStore` (singleton) — rate limiters, idempotency keys, demo sessions | Same session sometimes routes to different replicas, sees inconsistent state | Move to Redis; key by session ID; use `SET NX EX` for atomic idempotency claim |
| 4 | Saga work on checkout-svc | Should already work via MT competing consumers + EF optimistic concurrency | Verify under load (Item 2 in hiring plan) |
| 5 | L1 cache in catalog (if any `IMemoryCache` for product data) | Updates on one replica leave stale data in others | Verify Redis pubsub for `ProductCacheInvalidatedEvent` fires across all replicas |

## The AppHost change for the saga storm

For the saga storm flagship (Item 2 in the hiring plan) to be a
genuine multi-instance demo:

```csharp
var catalog = builder.AddProject<Projects.Catalog_Api>("catalog-svc")
    .WithReplicas(2)              // <-- competing consumers on stock reservation
    .WaitFor(catalogDb)
    .WithReference(catalogDb)
    .WithReference(rabbitmq);

var checkout = builder.AddProject<Projects.CheckoutOrchestrator_Api>("checkout-svc")
    .WithReplicas(2)              // <-- saga state machine running in N replicas
    .WaitFor(checkoutDb)
    .WithReference(checkoutDb)
    .WithReference(rabbitmq);

var payments = builder.AddProject<Projects.Payments_Api>("payments-svc")
    .WithReplicas(2)              // <-- payment session consumer in N replicas
    .WaitFor(paymentsDb)
    .WithReference(paymentsDb)
    .WithReference(rabbitmq);
```

BFF can stay at 1 instance for now (the ports are pinned for the
frontend). Or replicate it too and accept that the proxy port `:5050`
fronts N backend replicas (which is the correct production model).

After this change, every saga's events compete across N consumers per
service. The Aspire dashboard shows each replica's logs separately;
you can verify load-balancing by counting `Reserving stock for orderId=…`
log lines across catalog-svc replicas during a 50-saga storm.

## Verification checklist

For any service you replicate:

- [ ] Aspire dashboard (`https://localhost:17000`) shows N rows under
      that service name, each with its own dynamic port
- [ ] Each row has its own log stream
- [ ] An action that hits the service shows up in different replicas'
      logs over multiple invocations (load-balancing visible)
- [ ] A pubsub fanout (e.g. cache invalidation) fires the handler in
      all replicas, not just the one that received the trigger
- [ ] State that should be consistent across instances (idempotency,
      rate limits, breaker counters) IS consistent under concurrent
      access from a single client
- [ ] Killing one replica via the dashboard's "Stop" button doesn't
      bring the demo down — surviving replicas continue serving
- [ ] OTel spans from different replicas show distinct
      `service.instance.id` attributes in Tempo

## Anti-patterns to avoid

- **Don't add `WithReplicas` and call it done.** Replication without
  fixing in-process state silently degrades demo correctness — visitors
  see inconsistent counters, stale caches, breaker trips that "snap
  back" when the request lands on a different replica.
- **Don't replicate vault, postgres, redis, or rabbitmq containers.**
  They're already singletons by design; don't replicate them at the
  Aspire level. (Production replicas of these are clustering concerns
  outside Aspire's scope.)
- **Don't replicate identity-svc** without thinking about JWT signing
  key consistency. Replicas need the same signing key (which Vault
  provides) — verify before scaling.
- **Don't replicate to N>3 locally.** Each replica is a full .NET process;
  the local dev machine can run out of headroom fast. 2–3 per service
  is enough to prove distributed behaviour without thrashing the
  laptop.
- **Don't pin the BFF port and replicate it without testing.** The
  proxy-frontend / replica-backend split is correct in theory but
  needs verification with the actual portfolio-site under each replica
  to confirm SignalR sticky-routing, CORS, etc. behave.

## Where to look when something's wrong

- Aspire dashboard → resource view → click a replica → logs for that
  specific instance
- `logs/<service>-<hash>.log` files (per-replica, hash is the DCP run
  ID)
- `pgrep -fl "<service>.Api"` to confirm N processes are alive
- `lsof -i -P -n | grep <service>` to see which ports each replica is
  bound to
- For consumer-side debugging: enable OTel and look at the
  `service.instance.id` of spans coming back to the BFF — that tells
  you which catalog/payments/etc replica handled the call

## Cost of going multi-instance

- **Disk**: ~negligible (each replica is a process, not a copy of the
  binary on disk)
- **Memory**: each .NET service is ~150–300 MB; 2–3 replicas of 6
  services = ~3 GB additional RAM
- **CPU**: idle is fine; under load the host needs cores. 4 cores
  minimum for 2-replica saga storm with 50 sagas; 8 ideal.
- **Aspire startup time**: linear in replica count. 2x replicas ≈ 2x
  longer cold start. Mitigation: `--no-build` after first build.

For a developer laptop running the full stack + 2 BFF + 2 catalog + 2
checkout + 2 payments replicas, expect ~5 GB RAM in use and ~120s
cold-start. Workable.

## When NOT to replicate

- During initial bring-up or after major refactors — get the
  single-instance flow correct first.
- During payment integration debugging — having one replica makes
  webhook reception and state inspection simpler.
- For Vault rotation testing if it depends on a single-token-refresh
  path that doesn't account for replicas hitting the renewer at the
  same time.

The goal of replication is to PROVE distributed behaviour for the
demos that depend on it (cache invalidation, rate limiter, saga
storm). It's not a default. Single-instance is the simpler correct
state.
