# Observability Operations Guide

> How to monitor, diagnose, and operate the Haworks platform in production.

## Quick Start: "Something is broken, where do I look?"

```
┌─────────────────────────────────────────────────────────────────┐
│                    INCIDENT TRIAGE FLOW                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  1. Alert fires → Check Grafana "SLO Overview" dashboard        │
│     URL: http://grafana:3000/d/slo-overview                     │
│                                                                 │
│  2. Which service? → "Service Health" dashboard                 │
│     URL: http://grafana:3000/d/service-health                   │
│                                                                 │
│  3. What failed? → "Error Rates" dashboard (5xx by route)       │
│     URL: http://grafana:3000/d/error-rates                      │
│                                                                 │
│  4. Why? → Click trace_id in Loki → jumps to Tempo trace        │
│                                                                 │
│  5. Saga stuck? → "Saga State Machines" dashboard               │
│     URL: http://grafana:3000/d/saga-state-machines              │
│                                                                 │
│  6. Payment issue? → "Payment Flows" dashboard                  │
│     URL: http://grafana:3000/d/payment-flows                    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## Access Points

| Tool | URL | Purpose |
|------|-----|---------|
| **Grafana** | `http://grafana:3000` | Dashboards, alerts, log/trace exploration |
| **Prometheus** | `http://prometheus:9090` | Raw metrics, PromQL queries |
| **Tempo** | `http://tempo:3200` | Distributed traces |
| **Loki** | (via Grafana Explore) | Structured logs |
| **Alertmanager** | `http://alertmanager:9093` | Silence/acknowledge alerts |
| **RabbitMQ** | `http://rabbitmq:15672` | Queue inspection (backup UI) |

---

## Dashboards (What to look at when)

### 1. SLO Overview (`slo-overview`)
**When:** Daily standup, on-call rotation start, after deploy

Shows:
- Checkout + Payment success rate gauges (green/orange/red)
- Error budget burn rate (if line crosses 1.0 = burning too fast)
- Kafka consumer lag (search index freshness)
- Outbox pending count (event delivery health)
- RabbitMQ error queue count (consumer crashes)

### 2. Service Health (`service-health`)
**When:** Alert fires, deploy rollout, capacity planning

Shows:
- Service up/down status
- Request rate per service
- Error rate (%) per service
- P99 and P50 latency per service

### 3. Error Rates (`error-rates`)
**When:** HighErrorRate alert fires

Shows:
- 5xx by service (which service is broken)
- 4xx by service (client bugs or attacks)
- Error heatmap by route (which endpoint is failing)
- Live error logs from Loki (actual exception messages)

### 4. Saga State Machines (`saga-state-machines`)
**When:** SagaStuck alert fires, checkout complaints

Shows:
- Refunds stuck in RequiresReview (needs ops intervention)
- Checkouts stuck in RequiresReview (amount mismatch)
- Privacy erasure stalled (GDPR deadline risk)
- Saga transition rate and P95 duration

### 5. Payment Flows (`payment-flows`)
**When:** Payment complaints, revenue anomalies

Shows:
- Checkout sessions created rate
- Payment success vs failure
- Refund processing (stuck + completed)
- Subscription dunning (exhausted + renewed)
- Webhook processing latency

---

## Common Scenarios

### Scenario 1: "Checkout is slow"

1. Open **SLO Overview** → Check "Checkout P99 Latency" gauge
2. If red (>30s): Open **Service Health** → filter to `checkout`
3. Check if latency spike correlates with:
   - Catalog (stock reservation slow) → check catalog P99
   - Payments (session creation slow) → check payments P99
   - RabbitMQ (message delivery slow) → check outbox pending
4. Click into a slow request in Tempo:
   ```
   Grafana → Explore → Tempo → Search by service=checkout, minDuration=10s
   ```
5. The trace waterfall shows exactly which downstream call is slow

### Scenario 2: "Orders not appearing after payment"

This means the event pipeline is broken somewhere.

1. **SLO Overview** → Check "Outbox Pending Messages"
   - If high: Outbox relay is stalled. Check RabbitMQ connectivity.
2. **SLO Overview** → Check "RabbitMQ Error Queues"
   - If > 0: A consumer is crashing. Check which queue:
     ```
     Grafana → Explore → Loki → {service_name=~".*"} |= "DLQ"
     ```
3. **Saga State Machines** → Check if checkout saga is stuck
   - Stuck in `StockReservedState`: Payment service didn't respond
   - Stuck in `ReadyForPayment`: Customer abandoned or webhook lost

### Scenario 3: "Search results are stale"

1. **SLO Overview** → Check "Kafka Consumer Lag"
   - If > 1000: CDC consumer is behind
   - Look at search-svc logs:
     ```
     {service_name="search"} |= "CDC" | logfmt
     ```
2. If lag is 0 but results still stale:
   - Check if the product was updated in catalog (it may not have been)
   - Check Debezium connector status: `curl debezium:8083/connectors/catalog-connector/status`

### Scenario 4: "Refund is stuck"

1. **Saga State Machines** → "Refund Stuck in Review" stat
2. Query the specific refund:
   ```
   Grafana → Explore → Loki → {service_name="payments"} |= "RequiresReview"
   ```
3. Common causes:
   - Amount mismatch (provider refunded different amount)
   - Provider timeout (24h timeout fired)
   - Manual intervention needed (check Payments admin endpoint)

### Scenario 5: "Alert: RabbitMqErrorQueue"

This is critical — a consumer crashed on a message.

1. Find which queue:
   ```
   Grafana → Explore → Prometheus →
   rabbitmq_queue_messages{queue=~".*_error"} > 0
   ```
2. Read the fault details:
   ```
   Grafana → Explore → Loki →
   {service_name=~".*"} |= "DLQ" | json | exception_type != ""
   ```
3. If `is_transient=true`: Likely infrastructure blip, message will retry
4. If `is_transient=false`: Poison message — needs code fix or manual skip

---

## Log Queries (Copy-Paste Ready)

### Find errors for a specific service
```logql
{service_name="payments"} | logfmt | level="Error"
```

### Find all DLQ messages in the last hour
```logql
{service_name=~".*"} |= "DLQ" | json
```

### Follow a request by correlation ID
```logql
{service_name=~".*"} |= "abc123-correlation-id"
```

### Find slow MediatR handlers (>5s)
```logql
{service_name=~".*"} |= "MediatR" | json | duration > 5000
```

### Find payment webhook processing
```logql
{service_name="payments"} |= "webhook" | logfmt
```

---

## Trace Queries (Tempo)

### Find slow checkouts
```
service.name = "checkout-svc" AND duration > 10s
```

### Find failed payment webhooks
```
service.name = "payments-svc" AND status = error AND name =~ "payments.webhook.*"
```

### Follow a saga across services
```
Tags: saga.id = "<guid>"
```
This returns spans from checkout-svc, catalog-svc, payments-svc, orders-svc — the full saga lifecycle in one view.

---

## Loki → Tempo Drill-Down

Every log line includes `trace_id` (via `ActivityEnricher`). To jump from a log to its full distributed trace:

1. In Grafana Explore → Loki, find the error log
2. Click the `trace_id` field value
3. Grafana auto-navigates to Tempo with that trace
4. You see the full request waterfall across all services

---

## Alert Response Playbook

| Alert | Severity | First Action |
|-------|----------|--------------|
| `ServiceDown` | Critical | Check pod status: is it OOMKilled? Crashloop? |
| `HighErrorRate` | Warning | Open Error Rates dashboard → identify failing route |
| `RabbitMqErrorQueue` | Critical | Read DLQ logs → transient? Skip/retry. Permanent? Fix code. |
| `OutboxBacklogHigh` | Critical | Check RabbitMQ connectivity. If OK → check outbox relay pause. |
| `KafkaConsumerLagHigh` | Warning | Check search-svc is running. Restart if stuck. |
| `CheckoutErrorBudgetBurning` | Critical | Investigate immediately — SLO breach imminent in 2h. |
| `CheckoutSagaSlow` | Warning | Check downstream latency (catalog, payments). |
| `RefundStuckInReview` | Warning | Manual review needed — check Payments admin. |
| `PrivacyErasureStalled` | Critical | GDPR deadline risk — escalate to engineering lead. |
| `RabbitMqDown` | Critical | Infrastructure emergency — broker is unreachable. |

---

## Health Probe Endpoints (K8s Configuration)

```yaml
# Deployment spec
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
  initialDelaySeconds: 5
  periodSeconds: 10

readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
  initialDelaySeconds: 10
  periodSeconds: 5

startupProbe:
  httpGet:
    path: /health/startup
    port: 8080
  initialDelaySeconds: 0
  periodSeconds: 3
  failureThreshold: 30  # 90s max startup (migrations)
```

| Probe | Endpoint | What it checks |
|-------|----------|----------------|
| **Startup** | `/health/startup` | DB connectivity (migrations done) |
| **Liveness** | `/health/live` | Process alive (always passes) |
| **Readiness** | `/health/ready` | DB + RabbitMQ connected |

---

## Graceful Shutdown

When K8s sends SIGTERM:
1. Pod marked as terminating (removed from Service endpoints)
2. **5-second drain** — in-flight HTTP requests complete
3. MassTransit consumers finish current message (built-in)
4. Kafka consumer issues `Close()` (clean group leave, no rebalance lag)
5. Pod exits

---

## Adding Observability to a New Service

1. Call `builder.AddServiceDefaults()` in Program.cs (wires OTel, health, correlation ID)
2. Add `builder.Services.AddHealthChecks().AddDbHealthCheck<YourDbContext>()`
3. Create `YourActivities.cs`:
   ```csharp
   public static class YourActivities
   {
       public static readonly ActivitySource Source = new("Haworks.YourService", "1.0.0");
   }
   ```
4. Add your meter name to `ServiceDefaults.cs` (`.AddMeter("Haworks.YourService")` + `.AddSource(...)`)
5. Use Serilog with ActivityEnricher:
   ```csharp
   builder.Host.UseSerilog((ctx, cfg) =>
       cfg.ReadFrom.Configuration(ctx.Configuration)
          .Enrich.With(new ActivityEnricher())
          .WriteTo.Console());
   ```
6. That's it. OTel, correlation IDs, trace_id in logs, health probes — all automatic.

---

## Architecture Overview

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  Services    │────▶│ OTel Collector│────▶│    Tempo     │ (traces, 7d)
│ (.NET OTel)  │     │              │────▶│    Loki      │ (logs, 7d)
│              │     │              │────▶│  Prometheus  │ (metrics, 15s scrape)
└──────────────┘     └──────────────┘     └──────┬───────┘
                                                  │
                                           ┌──────▼───────┐
                                           │   Grafana    │
                                           │ (dashboards) │
                                           │              │──▶ Alertmanager
                                           └──────────────┘        │
                                                              ┌────▼────┐
                                                              │PagerDuty│ (critical)
                                                              │  Slack  │ (warning)
                                                              └─────────┘
```
