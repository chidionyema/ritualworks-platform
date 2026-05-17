# Platform Backlog — Production Readiness

> Last updated: 2026-05-17 | 22 services deployed, full observability stack

## How to use this document
- Items are ordered by priority within each tier
- **Effort**: S = days, M = 1-2 weeks, L = 1+ month
- Check off items as they're completed
- Move items between tiers as priorities shift

---

## Completed Items

- [x] **B-001: Observability stack** — Full OTel pipeline, 5 Grafana dashboards (SLO, service-health, error-rates, payment-flows, saga-state-machines), Prometheus + Loki + Tempo, Alertmanager → PagerDuty/Slack, saga/messaging/burn-rate alerts. PRs #193, #196, #198, #200
- [x] **B-002: Deploy all services** — All 22 services in Fly deploy script, ArgoCD applications, GitHub Actions CI/CD with path-filtered matrix. PR #202
- [x] **B-003: Pricing service** — PriceCalculationEngine (stateless singleton), tiered pricing, promotion codes, tax adapters (ConfigurableRate + RateTable), saga integration via PricingRequestedConsumer. Already implemented.
- [x] **B-004: BFF global rate limiting** — 3-tier: global per-IP sliding window (120/min), per-user token bucket (60/min), per-IP fixed window for expensive ops (10/min). In BFF Program.cs.
- [x] **B-006: Tax calculation** — ConfigurableRateTaxAdapter + RateTableTaxCalculator in Pricing service. Strategy pattern, fail-open option.
- [x] **B-008: Secrets rotation** — Vault Agent sidecar pattern (K8s/Fly/local), Scheduler lease watcher (hourly), JWT rotation (monthly with 15-min overlap). PR #207
- [x] **B-011: Alertmanager + DR runbook** — Alertmanager with PagerDuty/Slack routing, saga-alerts.yml + platform-alerts.yml, observability ops guide at docs/runbooks/observability-guide.md. PR #198
- [x] **Security hardening** — OWASP headers on BFF, ZAP nightly scan, CORS, rate limiting, file signature validation (Media). PR #204
- [x] **Performance/Scale** — k6 load tests (checkout + media), connection pool tuning (MaxPool=50, MinPool=5), Kafka consumer lag metrics. PR #204
- [x] **HWK analyzer violations** — All BuildingBlocks violations fixed (HWK019-050). PR #207
- [x] **Content→Media consolidation** — Single media service, FileSignatureValidator, batch upload, entity-linkage, S3 quarantine. PR #180
- [x] **Service READMEs** — All 22 services with HLD diagrams, API endpoints, events, domain models, edge cases, NFRs. PR #189
- [x] ~~Timeout consolidation (`HttpClientTimeoutOptions`)~~ — PR #121
- [x] ~~Architecture guard generalization~~ — PR #121
- [x] ~~Audit transient Postgres resilience~~ — PR #123
- [x] ~~Service-to-service JWT auth~~ — Identity service token + BFF forwarding
- [x] ~~JWKS validation fix (.NET 9 PostConfigure bug)~~ — Direct config in AddJwtBearer delegate
- [x] ~~104 security findings across 3 waves~~ — All resolved, 63+ arch guards
- [x] ~~Roslyn Architecture Analyzers HWK001-050~~ — 50 rules, all tests green

---

## Tier 0 — Critical (blocks production)

### B-005: Database backup automation
- **Effort**: M
- **Status**: Not started
- **Why**: Neon Postgres has automated backup but no verification. No restore testing, no documented RTO/RPO.
- **What to do**:
  - [ ] Verify Neon's automated backup coverage and PITR window
  - [ ] Add nightly backup verification job to CI (test restore to temp DB)
  - [ ] Document RTO/RPO targets (RTO < 1h, RPO < 5min)
  - [ ] Create disaster recovery runbook in `docs/DR-RUNBOOK.md`
  - [ ] Add Fly volume snapshot policy for Vault data
- **Risk if skipped**: No verified recovery path for data loss

---

## Tier 1 — High Priority (within 3 months)

### B-007: Centralized rate limiting BuildingBlock
- **Effort**: S (2-3 days)
- **Status**: BFF has it; not yet reusable
- **Why**: Identity has its own rate limiter, Privacy has its own. Pattern should be shared.
- **What to do**:
  - [ ] Extract `AddPlatformRateLimiting(IConfiguration)` to BuildingBlocks/Extensions
  - [ ] Read policies from `RateLimiting` config section
  - [ ] Wire into `AddServiceDefaults()` (opt-in per service)
  - [ ] Migrate Identity and Privacy to use the shared extension

### B-009: Admin / Backoffice portal
- **Effort**: L
- **Status**: API endpoints exist; no UI
- **Why**: Admin endpoints exist in Payments (refunds), Identity (users), Audit (export), Catalog (products), Merchant (approval), FeatureFlags (management). No unified frontend.
- **What to do**:
  - [ ] Choose framework (React Admin or Refine recommended)
  - [ ] Build views: Merchant approval, Refund processing, Order management, User management, Audit log viewer, Feature flag toggles, Rule management
  - [ ] Wire to existing admin API endpoints
  - [ ] Deploy as static site on Cloudflare Pages

### B-010: Fraud detection via RulesEngine
- **Effort**: M (1-2 weeks)
- **Status**: RulesEngine service exists with CRUD + evaluator; no rules seeded
- **Why**: RulesEngine has full expression evaluation, SQL injection guard, SafeTypeProvider. No fraud rules exist. CheckoutOrchestrator has no pre-payment risk check.
- **What to do**:
  - [ ] Design fraud rule set (velocity, card testing, geo anomaly, amount thresholds)
  - [ ] Seed rules via migration or admin endpoint
  - [ ] Add `FraudCheckRequestedEvent` / `FraudCheckPassedEvent` / `FraudCheckFailedEvent` to Contracts
  - [ ] Wire into CheckoutOrchestrator saga (between StockReserved and PaymentSessionRequested)
  - [ ] Add risk score to Payment entity
  - [ ] Alert on FraudCheckFailed (Prometheus counter + Alertmanager rule)
- **Dependencies**: RulesEngine (built), CheckoutOrchestrator saga, Payments

### B-013: Canary / blue-green deployments
- **Effort**: M (1-2 weeks)
- **Status**: All deploys are all-or-nothing (`flyctl deploy`)
- **Why**: No canary weight splitting, no smoke gate between canary and full rollout. A bad deploy goes to 100% immediately.
- **What to do**:
  - [ ] Use Fly Machines API for canary: deploy to 1 machine, health-check, then scale
  - [ ] Add smoke test step in deploy.yml (curl /health + key endpoint after canary)
  - [ ] Auto-rollback if smoke fails (flyctl releases rollback)
  - [ ] For K8s: Argo Rollouts with canary strategy + analysis template
  - [ ] Add canary success rate metric: compare canary machine errors vs baseline
- **Dependencies**: Observability (done — metrics exist to detect canary failures)

---

## Tier 2 — Medium Priority (3-12 months)

### B-012: Recommendation engine
- **Effort**: L
- **Why**: Analytics collects clickstream to Kafka. No ML pipeline, no "customers also bought".
- **Dependencies**: Analytics (clickstream), Search (serving), Catalog

### B-014: Customer support / ticketing integration
- **Effort**: M
- **Why**: No Zendesk/Freshdesk integration. Customer issues resolved manually.
- **Dependencies**: Identity (user lookup), Orders (order context)

### B-015: A/B testing framework
- **Effort**: M
- **Why**: FeatureFlags has percentage rollout. Analytics has clickstream. No experiment tracking or statistical significance.
- **Dependencies**: FeatureFlags (cohort), Analytics (metrics)

### B-016: Multi-tenancy BuildingBlock
- **Effort**: L
- **Why**: No `ITenantContext`, no row-level security. Merchant scoping is FK-based only.

### B-017: Shipping / fulfillment integration
- **Effort**: L
- **Why**: Address captured but no carrier integration (EasyPost/ShipEngine), no tracking.
- **Dependencies**: Orders, CheckoutOrchestrator, Location

### B-018: Web Push / APNs notification channel
- **Effort**: M
- **Why**: FCM push exists for sending. No VAPID web push, no APNs.
- **Dependencies**: Notifications service, Identity (device registration)

---

## Tier 3 — Low Priority (future)

### B-019: Event sourcing / CQRS read projections
- **Effort**: L | Current EF write-through + outbox is appropriate for current scale.

### B-020: Reporting / BI layer
- **Effort**: L | No data warehouse, dbt models, or self-serve reporting.

### B-021: Social features (follows, feeds, wishlists)
- **Effort**: L | Not core to marketplace.

---

## BuildingBlocks Gaps

| ID | Gap | Priority | Effort | Status |
|----|-----|----------|--------|--------|
| BB-01 | `AddPlatformRateLimiting()` shared extension | High | S | See B-007 |
| BB-02 | Circuit breaker Grafana dashboard | Low | S | Polly emits metrics; SLO dashboard covers it |
| BB-03 | Standardize migration orchestration | Low | S | StartupTaskRunner already handles this |
| BB-04 | Cache invalidation event contract | Low | S | ProductCacheInvalidatedEvent exists as pattern |
| BB-05 | Distributed lock abstraction | Low | M | Redis-based; needed for multi-instance coordination |
