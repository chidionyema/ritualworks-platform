# Dead Letter Queue (DLQ) Strategy

> Staff-level spec: how messages fail, how we detect, how we recover.

## Current Architecture

```
┌────────────���─┐    ┌──────────────────────┐    ┌─────────────────┐
│   Producer   │───▶│   Consumer Queue      │───▶│   Consumer      │
│ (outbox)     │    │   e.g. payment-       │    │   (3 retries)   │
└──────────────┘    │   session-requested   │    └────────┬────────┘
                    └──────────────────────┘             │
                                                         │ fails 3x
                                                         ▼
                    ┌──────────────────────┐    ┌─────────────────┐
                    │   Error Queue         │    │  Fault<T>       │
                    │   {queue}_error       │    │  published      │
                    └──────────────────────┘    └────────┬────────┘
                              │                          │
                              │                          ▼
                              │               ┌─────────────────────┐
                              │               │ GlobalFaultConsumer  │
                              │               │ • structured log     │
                              │               │ • fault metric       │
                              │               │ • transient classify │
                              │               └─────────────────────┘
                              │                          │
                              ▼                          ▼
                    ┌──────────────────────┐    ┌─────────────────────┐
                    │ Prometheus Alert      │    │ Domain FaultConsumer│
                    │ error queue > 0       │    │ • compensate        │
                    │ → PagerDuty           │    │ • escalate          │
                    └──────────────────────┘    └─────────────────────┘
```

## Current Retry Policy

```csharp
// BuildingBlocks/Messaging/MessagingServiceCollectionExtensions.cs
cfg.UseMessageRetry(r => r.Incremental(
    retryLimit: 3,
    initialInterval: TimeSpan.FromSeconds(1),
    intervalIncrement: TimeSpan.FromSeconds(2)));
// Total: 3 attempts, 1s → 3s → 5s = 9 seconds max before DLQ
```

## Gap Analysis

### Gap 1: GlobalFaultConsumer not registered in 7 services

**Severity**: High
**Impact**: Faults in Orders, Identity, Catalog, Notifications, Search, Webhooks, Audit are invisible at the bus level. Only the RabbitMQ error queue metric catches them — no structured log, no exception type breakdown.

**Services missing GlobalFaultConsumer**:
- `src/Orders/Orders.Infrastructure/DependencyInjection.cs`
- `src/Identity/Identity.Infrastructure/DependencyInjection.cs`
- `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs`
- `src/Notifications/Notifications.Infrastructure/DependencyInjection.cs`
- `src/Search/Search.Application/DependencyInjection.cs`
- `src/Webhooks/Webhooks.Infrastructure/DependencyInjection.cs`
- `src/Audit/Audit.Application/DependencyInjection.Capture.cs`

**Fix**:
```csharp
// In each service's MassTransit registration:
mt.AddConsumer<Haworks.BuildingBlocks.Messaging.GlobalFaultConsumer>();
```

**Time**: 5 minutes (mechanical — add one line per service)

---

### Gap 2: No replay mechanism

**Severity**: High
**Impact**: When a message lands in `_error` queue, the only way to retry it is via the RabbitMQ Management UI ("Move messages" button). No programmatic endpoint, no script, no audit trail of replays.

**What's needed**:
1. Admin API endpoint: `POST /admin/dlq/replay` (service-scoped or platform-wide)
2. Uses RabbitMQ Management HTTP API to move messages from `{queue}_error` back to `{queue}`
3. Logs every replay with operator identity + timestamp
4. Optional: replay specific MessageId only (not the entire queue)

**Fix** — add to BuildingBlocks or a dedicated admin service:
```csharp
[HttpPost("admin/dlq/replay")]
[Authorize(Roles = "Admin")]
public async Task<IActionResult> ReplayErrorQueue([FromBody] ReplayRequest request)
{
    // GET /api/queues/%2F/{queue}_error/get (fetch messages)
    // POST /api/exchanges/%2F/{queue}/publish (republish each)
    // DELETE from error queue after successful republish
    // Log: who, when, which queue, how many messages
}
```

**Time**: 2-4 hours (RabbitMQ Management API integration + tests)

---

### Gap 3: No poison message detection

**Severity**: Medium
**Impact**: If a message is structurally broken (malformed payload, missing required field, schema drift), it will fail → DLQ → operator replays → fails again → DLQ. Infinite loop of human effort.

**What's needed**:
1. Track `{MessageId} → failure_count` in Redis (TTL 7 days)
2. On each fault: increment counter
3. If counter > 3: move to quarantine queue instead of error queue
4. Alert: `poison_messages_quarantined_total > 0`

**Fix**:
```csharp
// In GlobalFaultConsumer, before logging:
var failCount = await redis.StringIncrementAsync($"dlq:fail:{context.MessageId}");
await redis.KeyExpireAsync($"dlq:fail:{context.MessageId}", TimeSpan.FromDays(7));

if (failCount > 3)
{
    logger.LogCritical("POISON MESSAGE: {MessageId} failed {Count} times — quarantined",
        context.MessageId, failCount);
    PoisonCounter.Add(1);
    // Message stays in _error queue but is flagged; operator knows not to replay
}
```

**Time**: 4 hours (Redis integration in GlobalFaultConsumer + metric + alert)

---

### Gap 4: No TTL / expiry on error queues

**Severity**: Low
**Impact**: Error queue messages accumulate forever. After months of operation, RabbitMQ memory grows. Stale messages from old schema versions sit alongside recent failures, confusing operators.

**What's needed**:
- Set `x-message-ttl: 604800000` (7 days) on all error queues
- Quarantine queue: 30-day TTL (forensics window)

**Fix** — RabbitMQ policy (no code change):
```bash
# Apply to all error queues
rabbitmqctl set_policy dlq-ttl ".*_error$" \
  '{"message-ttl": 604800000}' --apply-to queues

# Apply to quarantine queue
rabbitmqctl set_policy quarantine-ttl ".*_quarantine$" \
  '{"message-ttl": 2592000000}' --apply-to queues
```

Or declaratively in docker-compose/K8s init:
```yaml
# deploy/compose/rabbitmq-policies.json
[
  {"vhost": "/", "name": "dlq-ttl", "pattern": ".*_error$",
   "definition": {"message-ttl": 604800000}, "apply-to": "queues"},
  {"vhost": "/", "name": "quarantine-ttl", "pattern": ".*_quarantine$",
   "definition": {"message-ttl": 2592000000}, "apply-to": "queues"}
]
```

**Time**: 15 minutes

---

### Gap 5: No dead-letter alerting by message type

**Severity**: Medium
**Impact**: Alert fires on ANY error queue > 0. During a known transient spike (e.g., Stripe is down for 5 min), the alert fires repeatedly. Operators can't silence "Stripe timeout DLQs" without silencing ALL DLQ alerts. This leads to alert fatigue → missed real incidents.

**What's needed**:
1. Alertmanager route by queue label: route `queue=payment-*_error` to `#payments-oncall`
2. Separate alerts for transient vs permanent failures (using `is_transient` from GlobalFaultConsumer metric)
3. Throttle: only alert if error queue > 0 for > 5 minutes (transients clear faster)

**Fix** — update `infra/observability/alerts/platform-alerts.yml`:
```yaml
# Transient failures: warning only after 10 min (they usually self-resolve)
- alert: DlqTransientBacklog
  expr: |
    sum(rate(masstransit_faults_total{is_transient="True"}[10m])) by (consumer) > 0
    AND rabbitmq_queue_messages{queue=~".*_error"} > 5
  for: 10m
  labels:
    severity: warning

# Permanent failures: critical immediately
- alert: DlqPermanentFailure
  expr: |
    increase(masstransit_faults_total{is_transient="False"}[5m]) > 0
  for: 2m
  labels:
    severity: critical
```

**Time**: 30 minutes (alert rules + Alertmanager routing)

---

### Gap 6: Retry policy is one-size-fits-all

**Severity**: Medium
**Impact**: All consumers use the same 3-retry/9-second policy. But:
- External API calls (Stripe, EasyPost, Nominatim) can have 30s+ outages — 9s is too short
- Saga events that arrive before the DB is ready (race condition) need longer delays
- Audit writes (append-only, COPY-batched) can tolerate 60s delays without user impact

**What's needed**:
Per-consumer retry configuration via ConsumerDefinition. The pattern already exists (Catalog's `StockReleaseRequestedConsumerDefinition` has custom retries) but isn't applied to most consumers.

**Fix** — create retry presets in BuildingBlocks:
```csharp
// BuildingBlocks/Messaging/RetryPresets.cs
public static class RetryPresets
{
    /// <summary>Default: fast fail for internal events (3x, 9s total)</summary>
    public static void Fast(IRetryConfigurator r) => r.Incremental(3,
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

    /// <summary>External APIs: longer backoff for transient outages (5x, ~2.5min)</summary>
    public static void ExternalApi(IRetryConfigurator r) => r.Exponential(5,
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5));

    /// <summary>Saga events: handles race conditions (4x, ~30s)</summary>
    public static void SagaEvent(IRetryConfigurator r) => r.Incremental(4,
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

    /// <summary>Background/batch: tolerates long delays (3x, ~5min)</summary>
    public static void Background(IRetryConfigurator r) => r.Intervals(
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5));
}

// Usage in ConsumerDefinition:
protected override void ConfigureConsumer(
    IReceiveEndpointConfigurator endpoint,
    IConsumerConfigurator<PaymentWebhookValidatedConsumer> consumer)
{
    endpoint.UseMessageRetry(RetryPresets.ExternalApi);
}
```

**Time**: 2 hours (presets + apply to 5-6 key consumers)

---

### Gap 7: No delayed redelivery

**Severity**: High
**Impact**: After 3 immediate retries (9 seconds), the message goes directly to DLQ. There's no "try again later" stage. For external service outages (Stripe down for 20 minutes), this means hundreds of messages in DLQ that would succeed if retried in 5 minutes.

**What's needed**:
MassTransit's `UseDelayedRedelivery` — schedules the message for future delivery via the RabbitMQ delayed-message-exchange plugin (already configured in some services).

**Fix** — add to `ConfigureStandardRabbitMq`:
```csharp
public static void ConfigureStandardRabbitMq(
    this IRabbitMqBusFactoryConfigurator cfg,
    IBusRegistrationContext context)
{
    // Stage 1: Immediate retries (transient blips)
    cfg.UseMessageRetry(r => r.Incremental(
        retryLimit: 3,
        initialInterval: TimeSpan.FromSeconds(1),
        intervalIncrement: TimeSpan.FromSeconds(2)));

    // Stage 2: Delayed redelivery (service outages)
    cfg.UseDelayedRedelivery(r => r.Intervals(
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30)));

    cfg.ConfigureEndpoints(context);
}
```

**Resulting behavior**:
```
Attempt 1: immediate
Attempt 2: +1s
Attempt 3: +3s
--- immediate retries exhausted ---
Redelivery 1: +1 min
Redelivery 2: +5 min
Redelivery 3: +30 min
--- all redeliveries exhausted ---
→ Move to _error queue + publish Fault<T>
```

Total time before DLQ: ~36 minutes (instead of 9 seconds).

**Prerequisite**: RabbitMQ `rabbitmq_delayed_message_exchange` plugin must be enabled.
- docker-compose: already enabled (`rabbitmq-plugins enable rabbitmq_delayed_message_exchange`)
- Fly/CloudAMQP: check provider documentation
- K8s: Helm chart `rabbitmq.plugins` value

**Time**: 15 minutes (one function change in BuildingBlocks, verify plugin enabled)

---

## Target State (After Fixes)

```
Message published (via outbox — guaranteed)
    ↓
Immediate retry: 3 attempts (1s → 3s → 5s)
    ↓ still failing
Delayed redelivery: 3 attempts (1min → 5min → 30min)
    ↓ still failing (36 min total elapsed)
Move to _error queue
    ↓
Fault<T> published to bus
    ↓
┌─────────────────────────────────────────────────┐
│ GlobalFaultConsumer (registered in ALL services) │
│                                                 │
│ 1. Structured log (service, consumer, type,     │
│    exception, is_transient)                     │
│ 2. Increment masstransit.faults.total metric    │
│ 3. Poison detection: incr Redis fail counter    │
│    - If > 3 replays: mark as poison, alert      │
│ 4. If domain FaultConsumer exists: it also runs │
│    (compensation, escalation, event publish)     │
└─────────────────────────────────────────────────┘
    ↓
Alerting:
  - Transient: warning after 10 min (usually self-resolves)
  - Permanent: critical after 2 min
  - Poison: critical immediately
    ↓
Recovery:
  - Admin replay endpoint: POST /admin/dlq/replay
  - Audit trail: who replayed, when, which messages
  - Auto-purge: 7-day TTL on error queues, 30-day on quarantine
```

## Implementation Priority

| # | Fix | Effort | Impact | Do First |
|---|-----|--------|--------|----------|
| 1 | Register GlobalFaultConsumer in 7 services | 5 min | High | ✅ |
| 2 | Add delayed redelivery (UseDelayedRedelivery) | 15 min | High | ✅ |
| 3 | Error queue TTL (RabbitMQ policy) | 15 min | Low | ✅ |
| 4 | Split alerts (transient vs permanent) | 30 min | Medium | ✅ |
| 5 | Retry presets (per-consumer policies) | 2 hrs | Medium | Week 1 |
| 6 | Admin replay endpoint | 2-4 hrs | High | Week 1 |
| 7 | Poison message detection (Redis counter) | 4 hrs | Medium | Week 2 |

## Operational Runbook

### "I got a DLQ alert — what do I do?"

1. **Check Grafana** → "SLO Overview" → "Fault Rate by Exception Type" panel
2. **Is it transient?** (TimeoutException, HttpRequestException, NpgsqlException)
   - Yes → Check upstream service health. If recovering, messages will auto-replay via delayed redelivery.
   - Wait 30 min. If still failing after redelivery exhaustion → investigate root cause.
3. **Is it permanent?** (JsonException, ValidationException, InvalidOperationException)
   - Yes → This is a code bug or schema mismatch. Fix the code, deploy, then replay.
4. **Replay**:
   ```bash
   # Via RabbitMQ Management UI
   # Queue → {queue}_error → Get messages → Move messages (requeue)

   # Via admin endpoint (once implemented)
   curl -X POST https://bff/admin/dlq/replay -H "Authorization: Bearer $TOKEN" \
     -d '{"queue": "payment-webhook-validated_error", "count": 10}'
   ```
5. **Poison message?** (same MessageId keeps failing after replay)
   - Don't replay again. Inspect the message payload in RabbitMQ UI.
   - Likely: schema drift, missing required field, or consumer expects state that doesn't exist.
   - Fix: patch the data or update the consumer to handle the edge case.

### "How do I know if delayed redelivery is working?"

Check Grafana → Prometheus:
```promql
# Messages currently in delayed redelivery (not in error queue yet)
sum(rabbitmq_queue_messages{queue=~".*-delay-.*"})
```

If messages are accumulating in delay queues but never reaching the error queue, the redelivery system is working — the upstream service is still down but messages will be retried.

### "How do I add a domain-specific fault consumer?"

Follow the pattern in `StockReleaseFaultConsumer`:
```csharp
public sealed class MyFaultConsumer : IConsumer<Fault<MyEvent>>
{
    public Task Consume(ConsumeContext<Fault<MyEvent>> context)
    {
        // Access original message: context.Message.Message
        // Access exception: context.Message.Exceptions.FirstOrDefault()
        // Compensate, log critical, or publish failure event
    }
}

// Register in MassTransit config:
mt.AddConsumer<MyFaultConsumer>();
```

Use domain fault consumers when:
- You need **compensation** (release stock, cancel payment)
- You need to **publish a failure event** (downstream services need to know)
- You need **critical alerting** with business context (OrderId, SagaId)

GlobalFaultConsumer handles the generic case (metrics, logging). Domain consumers add business-specific behavior.
