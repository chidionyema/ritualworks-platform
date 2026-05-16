# Observability Platform — End-to-End Spec

**Status:** signed off 2026-05-16 — stack = OTel Collector + Prometheus + Loki + Grafana + Alertmanager, Tempo already operational
**Implementer:** Gemini CLI agents working brief-by-brief from `docs/agent-briefs/observability/`
**Reviewer:** Claude / user, between phases
**Target:** full observability stack reachable via `docker compose up`; all 15+ services visible in Grafana; saga alerts firing within 60 s of a stuck state

---

## 1. Goal & non-goals

**Goal.** Wire up the complete local observability stack so that every service emits metrics, logs, and traces to a single OTel Collector, which fans out to Prometheus (metrics), Loki (logs), and the already-running Tempo (traces). Grafana exposes unified dashboards across all three data sources. Alertmanager integrates with the existing `infra/observability/alerts/saga-alerts.yml` rule set and provides notification routing.

**Non-goals (v1):**
- Production / Fly.io deployment of the observability stack (Fly has its own OTLP secret — see `docs/runbooks/observability-fly-otlp-secret.md`)
- Grafana Cloud or any managed observability SaaS
- Continuous profiling (Pyroscope / pprof)
- Custom Grafana plugins or alerting via Grafana Alerting (we use Alertmanager)
- Log-based metrics (separate from direct instrumentation)
- Tracing for infrastructure components (Postgres, RabbitMQ, Kafka)

---

## 2. Architecture at a glance

```
  ┌─────────────────────────────────────────────────────────────────────┐
  │  All .NET services (15+)                                            │
  │  OpenTelemetry SDK — OTLP exporter → http://otel-collector:4318    │
  │  (traces + metrics + logs in a single exporter, fire-and-forget)   │
  └─────────────────┬───────────────────────────────────────────────────┘
                    │ OTLP/HTTP
                    ▼
  ┌─────────────────────────────────────────────────────────────────────┐
  │  otel-collector  (otelcol-contrib:0.101.0)                         │
  │  receivers:  otlp (grpc :4317, http :4318)                         │
  │  processors: batch, memory_limiter, resource (add host attrs),      │
  │              transform (PII redaction on logs)                      │
  │  exporters:                                                         │
  │    metrics → prometheusremotewrite → http://prometheus:9090         │
  │    logs    → loki        → http://loki:3100                        │
  │    traces  → otlp/grpc   → http://tempo:4317 (existing)            │
  │  extensions: health_check (:13133), pprof, zpages                  │
  └──────────┬───────────────────────┬────────────────────────────────-─┘
             │ Remote Write          │ OTLP/gRPC      │ Loki Push API
             ▼                       ▼                 ▼
  ┌──────────────────┐  ┌────────────────┐  ┌──────────────────────┐
  │  Prometheus      │  │  Tempo (exist) │  │  Loki                │
  │  :9090           │  │  :3200         │  │  :3100               │
  │  scrape: otel    │  └────────────────┘  │  retention: 7d       │
  │  + /metrics eps  │                      │  ingestion rate limit │
  │  alert rules     │                      └──────────────────────┘
  │  → alertmanager  │
  └──────────┬───────┘
             │ webhook
             ▼
  ┌──────────────────┐
  │  Alertmanager    │
  │  :9093           │
  │  saga-alerts.yml │
  │  + routing rules │
  └──────────────────┘
             │
             ▼
  ┌──────────────────────────────────────────────────────┐
  │  Grafana  :3000                                       │
  │  DS: Prometheus, Loki, Tempo (all auto-provisioned)  │
  │  Dashboards (version-controlled JSON):               │
  │   • service-health.json                              │
  │   • saga-state-machines.json                         │
  │   • payment-flows.json                               │
  │   • error-rates.json                                 │
  └──────────────────────────────────────────────────────┘
```

**Why OTel Collector as a fan-out hub.** Services emit one signal stream over OTLP; the collector buffers, batches, redacts PII, and fans out to multiple backends. Backends can be swapped (e.g., Prometheus → Thanos) without touching service code. If the collector crashes, services drop telemetry silently — they never block on it.

**Why Prometheus remote-write (not scrape-only).** The OTel Collector's `prometheusremotewrite` exporter pushes metrics on a push model, which means services do not need to expose `/metrics` on a reachable port inside Docker compose. Prometheus also still scrapes its own `otel-collector:8888` metrics endpoint and the individual service `/metrics` endpoints for any metrics not flowing through OTLP (e.g., Kestrel internals emitted by `UseOpenTelemetryPrometheusScrapingEndpoint`).

**Why Loki for logs.** Serilog already writes to the console in JSON; the OTel log receiver ingests those and pushes to Loki via the `loki` exporter. No Promtail or agent sidecar needed.

---

## 3. Service inventory

The following services exist in `deploy/compose/docker-compose.yml` and must all appear in dashboards:

| Container name           | Service slug         | Port (internal) |
|--------------------------|----------------------|-----------------|
| `rw-identity-svc`        | `identity`           | 8080            |
| `rw-catalog-svc-1`       | `catalog`            | 8080            |
| `rw-catalog-svc-2`       | `catalog`            | 8080            |
| `rw-orders-svc`          | `orders`             | 8080            |
| `rw-payments-svc`        | `payments`           | 8080            |
| `rw-checkout-svc`        | `checkout`           | 8080            |
| `rw-content-svc`         | `content`            | 8080            |
| `rw-search-svc`          | `search`             | 8080            |
| `rw-audit-svc`           | `audit`              | 8080            |
| `rw-webhooks-svc`        | `webhooks`           | 8080            |
| `rw-bff-web`             | `bff-web`            | 8080            |
| + any future services    | via OTLP auto-discovery |              |

Multi-instance services (e.g., `catalog-svc-1`, `catalog-svc-2`) must carry a `service.instance.id` resource attribute so Grafana can distinguish them. This is set in each service's OTel SDK bootstrap via `OTEL_RESOURCE_ATTRIBUTES=service.instance.id=<container_name>` environment variable, injected in docker-compose (brief O6).

---

## 4. Contracts & configuration surfaces

### 4.1 OTel Collector (`infra/observability/otel-collector/config.yaml`)

Single config file. Mounted read-only into the container.

Key pipeline decisions:
- **Memory limiter** (400 MiB soft / 500 MiB hard) — prevents OOM killing the collector under burst load.
- **Batch processor** — 8192 items or 5 s timeout, whichever comes first. Reduces per-request overhead to Loki/Prometheus.
- **PII redaction** — `transform` processor on the `logs` pipeline: replace `email`, `password`, `credit_card`, `token` field values with `[REDACTED]` using OTTL `replace_pattern`.
- **Resource detection** — `resourcedetection` processor adds `host.name`, `os.type` from the host environment.
- **Metrics endpoint** — collector exposes its own Prometheus metrics at `:8888/metrics` so Prometheus can scrape it.

### 4.2 Prometheus (`infra/observability/prometheus/prometheus.yml`)

Static config for local compose (no service discovery needed):
- `remote_write` target is replaced by the collector pushing via `prometheusremotewrite`.
- Prometheus scrapes:
  1. `otel-collector:8888` — collector self-metrics.
  2. Each service's `http://<container>:8080/metrics` — Kestrel + ASP.NET + CLR metrics not in OTLP.
- Rule files: `infra/observability/alerts/saga-alerts.yml` (existing) + new `infra/observability/alerts/platform-alerts.yml`.
- Retention: `--storage.tsdb.retention.time=15d` CLI flag in compose.

### 4.3 Loki (`infra/observability/loki/loki-config.yaml`)

Single-binary mode (no microservices). Key settings:
- `ingestion_rate_mb: 8` per tenant, `ingestion_burst_size_mb: 16`.
- Retention: 7 days (`retention_period: 168h`).
- **Drop vs backpressure:** Loki is configured with `max_streams_per_user: 5000`. The OTel Collector's Loki exporter uses `retry_on_failure` with `max_elapsed_time: 30s`; after that, logs are dropped. Services never block.
- Storage: local filesystem at `/loki` (bind-mounted volume).

### 4.4 Grafana (`infra/observability/grafana/`)

Three sub-directories, all auto-provisioned on startup (no manual UI clicks):

```
infra/observability/grafana/
  provisioning/
    datasources/
      datasources.yaml        ← Prometheus + Loki + Tempo
    dashboards/
      dashboards.yaml         ← points to /var/lib/grafana/dashboards/
  dashboards/
    service-health.json
    saga-state-machines.json
    payment-flows.json
    error-rates.json
```

Grafana admin credentials for local dev: `admin` / `admin` (set via `GF_SECURITY_ADMIN_PASSWORD=admin`).

### 4.5 Alertmanager (`infra/observability/alertmanager/alertmanager.yml`)

Routes:
- `severity=critical` → PagerDuty integration key (env var `ALERTMANAGER_PAGERDUTY_KEY`, empty by default in local dev — falls through to log-only receiver).
- `severity=warning` → Slack webhook (env var `ALERTMANAGER_SLACK_WEBHOOK`, optional).
- Default receiver: `log-only` (prints to stdout, always present, no external dependencies).

Inhibition rules:
- If `severity=critical` fires for a service, suppress `severity=warning` alerts for the same `job` label.

---

## 5. Data flow & failure modes

| Failure scenario | Detection | Mitigation |
|---|---|---|
| OTel Collector crash | Collector healthcheck fails; Prometheus alert `OtelCollectorDown` | Services continue; OTLP export returns error (fire-and-forget SDK config); no service disruption |
| Prometheus storage full (>15 GiB) | `DiskPressure` alert (prometheus self-metric `prometheus_tsdb_head_chunks_storage_size_bytes`) | Alert fires; operator runs `docker compose restart prometheus`; auto-compaction reclaims space within 5 min |
| Loki ingestion rate exceeded | Collector logs 429 from Loki; `LokiIngestionRateHigh` alert | OTel Collector drops excess logs after 30 s retry budget; logs lost but services unaffected |
| Grafana provisioning drift | Dashboard JSON is version-controlled; any manual edit is overwritten on restart | Run `docker compose restart grafana` to restore; dashboards are idempotent |
| Multi-instance metric collision | Two catalog instances emit identical `job` label | `service.instance.id` resource attribute maps to `instance` label via collector `resource` processor; Grafana aggregates with `sum by (service_name)` |
| Sensitive data in logs | PII fields appear in log body before redaction | OTel `transform` processor runs before Loki exporter; redacts email/password/token/credit_card patterns via OTTL regex |
| Alertmanager has no external destination | `ALERTMANAGER_PAGERDUTY_KEY` is empty | `log-only` receiver always present as fallback; alert is not silently dropped |
| Tempo already running on ports 4317/4318 | Port conflict with OTel Collector if collector also binds 4317 | Collector binds only `0.0.0.0:4317` (gRPC) and `0.0.0.0:4318` (HTTP) on the compose network; Tempo keeps its existing ports; services send OTLP to collector, collector forwards to Tempo |

---

## 6. SLA targets

| Signal | Target | How measured |
|---|---|---|
| Metric scrape interval | 15 s | Prometheus `scrape_interval` |
| Alert evaluation interval | 15 s | Prometheus `evaluation_interval` |
| Alert-to-fire latency (saga stuck) | < 60 s from 5-min threshold | End-to-end: inject stuck saga metric, wait for Alertmanager POST to log receiver |
| Log ingestion lag p99 | < 5 s from log emission | Loki `loki_ingester_chunk_flush_duration_seconds` |
| Grafana dashboard load | < 2 s | Browser network tab (manual acceptance check) |
| Collector memory ceiling | < 500 MiB | `memory_limiter` hard limit; OOM kills collector, not services |

---

## 7. Instrumentation conventions (enforced, not implemented by these briefs)

These conventions must already be present in service code (via `BuildingBlocks.Telemetry`). The observability briefs assume they are in place; if a service deviates, that is a separate remediation task.

- **OTLP endpoint:** all services read `OTEL_EXPORTER_OTLP_ENDPOINT` from env (default `http://otel-collector:4318`).
- **Resource attributes:** `service.name`, `service.version`, `service.instance.id` set at SDK init.
- **Metric naming:** `<service>_<noun>_<unit>` e.g., `payments_refund_stuck_in_review_total`. Saga-specific metrics already defined in `saga-alerts.yml`.
- **Log format:** Serilog JSON, all services. `Application` property = service slug.
- **Span naming:** `HTTP <METHOD> <route_pattern>` for HTTP; `<exchange>/<queue> receive` for MassTransit consumers.

---

## 8. Implementation plan (Gemini CLI agents)

Six self-contained briefs in `docs/agent-briefs/observability/`. Each is one Gemini CLI invocation. Hard checkpoints between phases.

```
Phase 1: Core pipeline (1 agent, sequential — blocks all dashboards and alerts)
  O1  OTel Collector config + container in compose
  O2  Prometheus config + container in compose
  O3  Loki config + container in compose
      → CHECKPOINT: docker compose up otel-collector prometheus loki;
        curl http://localhost:13133/ returns 200 (collector health);
        curl http://localhost:9090/-/ready returns 200;
        curl http://localhost:3100/ready returns 200.

Phase 2: Three independent tracks — fire all three in parallel
  O4  Grafana dashboards (provisioning configs + 4 dashboard JSONs)
  O5  Alertmanager config + integration with saga-alerts.yml
  O6  docker-compose.yml wiring (OTEL env vars on all services + volumes)
      → CHECKPOINT: docker compose up (full stack);
        Grafana at :3000 shows all 4 dashboards pre-loaded;
        all 15+ services appear in service-health dashboard within 2 min;
        saga-alerts.yml rules appear in Prometheus /alerts;
        Alertmanager at :9093 shows routing tree.
```

**Anti-stuck rules for every brief:**

1. Read **Inputs** before writing any file. Don't guess paths.
2. Stay inside **Deliverable** scope. Out-of-scope observations go in done-report only.
3. **Acceptance** commands are non-negotiable. If they fail for a reason outside your control, write a `blocker:` line and stop.
4. Hard time budget: ~30 min per brief. Emit a blocker if stuck past 30 min.
5. **No cross-brief edits.** O4 must not touch prometheus.yml; that's O2's file. Raise a blocker if O2's output is incomplete.
6. Done-report format is fixed (see `docs/agent-briefs/audit-protocol.md`). Prose is not acceptable.

---

## 9. Test plan

### 9.1 Smoke (manual, post `docker compose up`)

| Check | Command | Expected |
|---|---|---|
| Collector health | `curl -s http://localhost:13133/` | `{"status":"Server available"}` |
| Prometheus ready | `curl -s http://localhost:9090/-/ready` | `Prometheus Server is Ready.` |
| Prometheus targets | `curl -s http://localhost:9090/api/v1/targets \| jq '.data.activeTargets[].health'` | all `"up"` |
| Loki ready | `curl -s http://localhost:3100/ready` | `ready` |
| Grafana login | `curl -su admin:admin http://localhost:3000/api/health` | `{"database":"ok"}` |
| Grafana dashboards | `curl -su admin:admin http://localhost:3000/api/search` | 4 dashboards returned |
| Alertmanager | `curl -s http://localhost:9093/-/healthy` | `OK` |
| Alert rules | `curl -s http://localhost:9090/api/v1/rules \| jq '.data.groups[].name'` | includes `"saga-health"` |

### 9.2 Alert firing test

```bash
# Inject a synthetic metric that triggers RefundStuckInReview
curl -s -X POST http://localhost:9091/metrics/job/test \
  --data-binary 'payments_refund_stuck_in_review_total 1'

# Wait 65 seconds (5m for threshold is waived in test — use a test-only rule)
# Check Alertmanager received it
curl -s http://localhost:9093/api/v2/alerts | jq '.[].labels.alertname'
# expected: "RefundStuckInReview"
```

Note: the 5-minute `for:` clause in saga-alerts.yml means a true end-to-end test takes >5 min. The acceptance criterion is that the alert appears in Alertmanager within 60 s of the `for:` timer expiring. Brief O5 includes a test-only alert rule with `for: 0m` to validate the routing path without the 5-min wait.

### 9.3 Log ingestion test

```bash
# Trigger any log-emitting endpoint
curl -s http://localhost:5050/health

# Query Loki for recent logs from bff-web
curl -sG http://localhost:3100/loki/api/v1/query \
  --data-urlencode 'query={service_name="bff-web"}' \
  --data-urlencode 'limit=5' \
  | jq '.data.result[0].values[0][1]'
# expected: a JSON log line from bff-web
```

---

## 10. Failure modes & runbook stubs

| Failure | Detection | Mitigation |
|---|---|---|
| Collector not receiving from a service | `otelcol_receiver_refused_spans{service_name="X"}` counter rising | Check `OTEL_EXPORTER_OTLP_ENDPOINT` env var on that container; verify collector is healthy |
| Prometheus not scraping a target | Target shows `down` in `:9090/targets` | Check container network — all services must be on the same compose network as Prometheus |
| Loki query returns no results | Query logs from Grafana Explore | Check `{service_name="X"}` label exists; check collector Loki exporter logs for 4xx errors |
| Grafana dashboards missing | Dashboard panel shows "No data" | Verify provisioning YAML path; `docker compose restart grafana`; check `/var/lib/grafana/dashboards/` inside container |
| Alertmanager not routing | Alert fires in Prometheus but absent in Alertmanager UI | Check `alerting.alertmanagers` in prometheus.yml points to `alertmanager:9093`; check Alertmanager route config |
| Disk full (Loki data) | `no space left on device` in Loki logs | Increase `retention_period` enforcement (compaction job); or delete volume and restart: `docker compose down -v loki && docker compose up -d loki` |

---

## 11. Sign-off (2026-05-16)

| Question | Decision |
|---|---|
| Metrics push model | OTel Collector → Prometheus remote-write (not scrape-only) |
| Log transport | OTel log receiver → Loki exporter (no Promtail) |
| Trace backend | Tempo (already operational); collector forwards OTLP |
| Dashboard provisioning | Version-controlled JSON in `infra/observability/grafana/dashboards/` |
| PII redaction | OTel `transform` processor with OTTL regex; before Loki exporter |
| Alert routing fallback | `log-only` receiver always present; no silent drops |
| Multi-instance labels | `service.instance.id` via `OTEL_RESOURCE_ATTRIBUTES` env in compose |
| Implementer | Gemini CLI agents, brief-by-brief |
| Reviewer | Claude / user, between phases |
