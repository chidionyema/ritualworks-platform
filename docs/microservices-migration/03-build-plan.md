# 03 — Build Plan: 8 Weeks, Greenfield

## Strategy: Clean-Slate Greenfield

Not strangler fig. Not migration. **Build the right system from scratch in a fresh repo, with the existing modular monolith open in another tab as a code-cribbing reference.**

Why this works for our context:
- The existing monolith is a **portfolio prototype**, not a live system. No customers, no traffic, no SLA, no rollback urgency.
- The existing monolith already validated the domain logic, the EF mappings, the validators, the security middleware. We don't have to *invent* — we have to *recompose* into the right shape.
- Success criteria is binary and testable: **all tests pass + local deploy works**. No "is the cutover safe" headache.

What disappears compared to a real-world migration:
- YARP routing between old and new
- `Legacy/` holding pen
- Feature flags between backends
- Dual-write / soak periods
- Cross-DB FK audits (no FKs in the new schema to begin with)
- HS256 → RS256 dual-validation
- Conditional fixtures
- "Copy first, delete later" test migration

What stays: every architectural decision in [01-architecture.md](./01-architecture.md), [02-platform.md](./02-platform.md), and the ADRs. They're all about the *target* system; only the path to get there changes.

See [adr/0008-clean-slate-greenfield.md](./adr/0008-clean-slate-greenfield.md) for the full decision rationale.

---

## Build Order: Vertical Slice First, Then Repeat the Pattern

Build **one complete service end-to-end** in Phase 1 — code, DbContext, migrations, unit tests, integration tests, contract tests, architecture tests, Helm chart, ArgoCD Application, Aspire wiring, Vault setup, observability, README section. **That becomes the canonical template.** The other 6 services then clone the pattern.

| # | Week | Service | Why this position |
|---|---|---|---|
| 0 | 1 | **Foundation** | Repo skeleton, `Haworks.Contracts` + `Haworks.BuildingBlocks`, Aspire AppHost shell, kind cluster + ArgoCD bootstrap, Pact broker, observability stack. |
| 1 | 2–3 | **identity-svc** | The vertical-slice template. Simplest service (leaf, no inbound events) but used by every other service. JWT signing, JWKS endpoint, gRPC API. |
| 2 | 4 | **catalog-svc** | Second leaf. Proves the messaging pattern (publishes `StockReserved` etc.). Stock reservation gRPC for the saga. |
| 3 | 5 | **payments-svc** | Proves webhook ingress (Stripe + PayPal) and per-context outbox publishing. |
| 4 | 6 | **orders-svc** | First service with both inbound + outbound events. Owns Order aggregate. |
| 5 | 7 | **checkout-orchestrator-svc** | The saga. All upstream services already exist as event producers. **Crown jewel.** |
| 6 | 7–8 | **content-svc** | MinIO + ClamAV. Self-contained. |
| 7 | 8 | **bff-web** | Composes everything via gRPC. SignalR. The public HTTP edge. |

8 weeks total. Each phase is independently mergeable to `main` once its acceptance criteria are met.

---

## Phase 0 — Foundation (Week 1)

Build the repo skeleton + the cross-cutting infrastructure that every service will depend on. **No service code yet** — the goal is a working pipeline that proves the toolchain works end-to-end.

### What

1. **Create the new monorepo:**
   ```
   haworks-platform/
   ├── src/
   │   ├── Identity/                    (empty .sln + boundary tests)
   │   ├── Catalog/                     (empty)
   │   ├── Orders/                      (empty)
   │   ├── Payments/                    (empty)
   │   ├── Content/                     (empty)
   │   ├── CheckoutOrchestrator/        (empty)
   │   ├── BffWeb/                      (empty)
   │   ├── Contracts/                   (Haworks.Contracts.csproj — event records, .proto)
   │   └── BuildingBlocks/              (Haworks.BuildingBlocks.csproj)
   ├── tests/
   │   ├── E2E/                         (Playwright skeleton)
   │   └── Architecture/                (MonorepoBoundaryTests.cs)
   ├── deploy/
   │   ├── aspire/                      (AppHost project — infra resources only for now)
   │   ├── helm/                        (empty per-service folders)
   │   └── argocd/                      (App-of-Apps + applications/)
   ├── infra/
   │   ├── pact-broker/                 (docker-compose.yml + Helm)
   │   └── observability/               (otel-collector, tempo, loki, prometheus, grafana)
   ├── scripts/
   │   ├── seed-vault-dev.sh            (cribbed from old monolith, parameterized per service)
   │   ├── k8s-up.sh                    (kind + ArgoCD bootstrap)
   │   └── local-deploy-smoke.sh        (the canonical "is local deploy working" test)
   ├── docs/microservices-migration/    (the documentation already written — copy in)
   ├── .github/workflows/
   │   ├── ci.yml                       (path-aware: per-service jobs + cross-cutting jobs)
   │   └── reusable-dotnet-service.yml  (called by per-service jobs)
   ├── Directory.Build.props            (boundary enforcement at root)
   └── README.md                        (the killer landing page)
   ```

2. **Build `Haworks.BuildingBlocks` NuGet** by lifting these from the monolith:
   - `Result<T>`, `Error`, `IDomainEvent`, `AuditableEntity`, `BaseEntity`
   - MediatR pipeline behaviors (validation, logging, idempotency)
   - `IDomainEventPublisher` + `MassTransitDomainEventPublisher`
   - `BoundedContextConsumerDefinition<T,TDb>` and saga variant
   - `VaultConfigBootstrap`, `DynamicCredentialsConnectionInterceptor`
   - `AddServiceDefaults()` (lifted + renamed from `src/haworks.ServiceDefaults`)
   - `AddMessaging<TDbContext>()` extension that registers MT + outbox + OTel instrumentation **non-optionally** (fixes Risk 6)
   - `TestWait.Until` / `TestWait.NotHappens` (currently in `tests/_Infrastructure/TestWait.cs`)

3. **Build `Haworks.Contracts` NuGet** with placeholder records — actual events get added per-phase as services come online.

4. **Stand up Aspire AppHost shell** (`deploy/aspire/Program.cs`) with infra-only resources:
   - postgres (with empty database list — services add their own)
   - redis
   - rabbitmq (pinned port 5672)
   - vault (dev mode) + vault-init + vault-seed (per-service AppRole topology from day 1)
   - minio (deferred until content-svc, but wire it up so we don't forget)
   - clamav (same)
   - **pact-broker** (new) + its postgres sidecar

5. **Stand up kind + ArgoCD:**
   - `make k8s-up` provisions a kind cluster, installs ArgoCD, applies App-of-Apps.
   - ArgoCD UI port-forwarded to localhost:8080.
   - Vault deployed via Helm with K8s auth method enabled.
   - Observability stack (Tempo, Loki, Prometheus, Grafana) deployed via Helm.
   - Pact broker deployed via Helm.
   - **No service applications yet** — they're added per-phase.

6. **Stand up CI:**
   - `.github/workflows/ci.yml` with `dorny/paths-filter@v3` — path → triggered jobs map.
   - `reusable-dotnet-service.yml` — unit + integration + Pact-publish + image-build template, called per service.
   - Required PR check: `pact-broker can-i-deploy --to-environment production` (tautologically true with no services yet — proves the wiring).

7. **Architecture test:** `tests/Architecture/MonorepoBoundaryTests.cs` — asserts all 7 service folders exist as `.sln`s and each has a boundary test scaffold.

### Pre-req
A new GitHub repo. The existing monolith stays in its current location, untouched, as a reference.

### Test posture
- `tests/Architecture/MonorepoBoundaryTests.cs` green.
- `dotnet run --project deploy/aspire` brings up infra resources cleanly. `vault-seed` writes secrets. `pact-broker` reachable at `localhost:9292`.
- `make k8s-up` brings up kind + ArgoCD + observability + Pact broker. ArgoCD UI shows infra applications synced.
- `scripts/local-deploy-smoke.sh` (placeholder — just hits `pact-broker /diagnostic/status/heartbeat` for now) returns 0.
- CI: empty PR runs the workflow successfully.

### Done
- All boilerplate exists, none of it is wrong.
- A new contributor can clone the repo and run `dotnet run --project deploy/aspire` and `make k8s-up` and have working infra in <5 min.
- The first service (Phase 1) has a clean canvas to land on.

---

## Phase 1 — identity-svc (Weeks 2–3)

The **vertical-slice template**. This phase is intentionally larger than subsequent ones because we're inventing the pattern as much as building the service. Whatever we settle on here, the next 6 services clone.

### What

1. **Service code under `src/Identity/`:**
   - `Identity.Domain` — User, Role, RefreshToken aggregates
   - `Identity.Application` — commands (Login, Register, RefreshToken, RevokeToken, UpdateProfile), queries (GetUser, GetUserLogins), handlers, validators
   - `Identity.Infrastructure` — `IdentityDbContext`, EF migrations, Vault `DynamicCredentialsConnectionInterceptor` wiring, RSA keypair management (private key in Vault `secret/identity/jwt-signing`), JWKS publishing
   - `Identity.Api` — REST endpoints (`/auth/login`, `/auth/register`, `/auth/refresh`, `/.well-known/jwks.json`), gRPC service (`IntrospectToken`, `GetUser`)
2. **JWT from day 1: RS256 + JWKS endpoint.** No HS256, no dual-validation, no migration window.
3. **Events published:** `UserRegistered`, `UserProfileChanged`, `JwtSigningKeyRotated`. Records added to `Haworks.Contracts.Identity.v1.*`.
4. **Helm chart at `deploy/helm/identity-svc/`** — Deployment, Service, ServiceAccount (Vault K8s auth bound), NetworkPolicy, ServiceMonitor, PDB. `values.yaml` (kind defaults) + `values.prod.yaml` (EKS overrides).
5. **ArgoCD Application at `deploy/argocd/applications/identity-svc.yaml`** pointing at the Helm chart.
6. **Aspire wiring** in `deploy/aspire/Program.cs`: adds `identity-svc` as a project reference (with `--override identity=image` for image-mode), wires Vault credential paths, wires identity DB.
7. **Tests:**
   - `tests/Identity.Unit/` — handler tests, validator tests, domain invariants. Crib heavily from monolith's `tests/haworks.Tests.Unit/Commands/Auth/*` and `Domain/Auth/*` — same logic, same mocks.
   - `tests/Identity.Integration/` — Testcontainers (Postgres + Redis), full-stack endpoint tests. Crib from monolith's `tests/haworks.Tests.integration/Controllers/AuthenticationControllerTests.cs` etc.
   - `tests/Identity.Contract/` — Pact: `JwtIntrospectionContract.cs` (gRPC HTTP Pact), `UserProfileChangedEventContract.cs` (message Pact). Identity is the producer for both.
   - `tests/Identity.Architecture/BoundaryTests.cs` — NetArchTest enforces "may only reference Contracts + BuildingBlocks."
8. **Vault wiring:** `infra/vault/policies/svc-identity.hcl` + AppRole + KV path namespace (`secret/identity/*` + `secret/shared/jwt/*` writable by identity, readable by all).
9. **README section:** `docs/services/identity-svc.md` — what it owns, how to run it standalone, how to test it.

### Pre-req
Phase 0 done.

### Test posture
- All identity-specific test suites green in CI.
- `pact-broker can-i-deploy --pacticipant identity-svc --to-environment production` passes.
- `dotnet run --project deploy/aspire` brings up identity-svc cleanly. `curl https://localhost:<port>/.well-known/jwks.json` returns the public key.
- `make k8s-up` syncs identity-svc Application via ArgoCD; pod healthy; JWKS endpoint reachable through ingress.
- E2E (Playwright): register → login → call protected endpoint with token → token validates against JWKS.

### Done
- The full vertical slice exists and is documented.
- The pattern is canonical — Phase 2 starts by copying this folder structure and replacing the domain.
- README hero diagram now shows identity-svc as the first node in the system map.

---

## Phase 2 — catalog-svc (Week 4)

### What

1. Service code under `src/Catalog/` cribbing from monolith's `src/Domain/Entities/Catalog/`, `src/Application/Commands/Catalog/`, `src/Infrastructure/Repositories/Catalog/`.
2. **`IStockService`** lifted and refactored to publish events via the per-context outbox: `StockReserved`, `StockReservationFailed`, `StockReleased`. Drops the custom `IStockRecoveryQueue` shim — RabbitMQ DLQ replaces it.
3. **gRPC API** for stock reservation (sync precondition for the saga): `ReserveStock(orderId, items[])`, `ReleaseStock(orderId)`.
4. **REST API** for product catalog browse.
5. Same wiring as Phase 1: Helm chart, ArgoCD app, Aspire wiring, Vault policies, contract tests (consumer + provider for stock events; provider for `ReserveStock` gRPC).
6. **Cross-service contract:** Pact tests assert catalog produces `StockReservedEvent` shape that orders-svc + checkout-orchestrator-svc will eventually consume. Generated by **placeholder consumer tests** living in `tests/Catalog.Contract/_FutureConsumers/` until those services exist.

### Pre-req
Phase 1 done. Identity gRPC available for Catalog's authorization checks.

### Test posture
- All catalog-specific tests green.
- Concurrent stock reservation test green (verified with 100 parallel reserves of last-in-stock item — exactly one succeeds).
- `pact-broker can-i-deploy --pacticipant catalog-svc` passes.
- E2E: browse catalog → reserve stock via gRPC → assert stock count decremented atomically.

### Done
- catalog-svc deployed via ArgoCD, healthy.
- Stock-related Pacts published to broker.

---

## Phase 3 — payments-svc (Week 5)

### What

1. Service code under `src/Payments/` cribbing from monolith's `src/Domain/Entities/Payments/`, `src/Infrastructure/Payments/{Stripe,PayPal}/`.
2. **No `IOrderRepository` injection** in `StripePaymentProcessor`/`PayPalPaymentProcessor` — they publish `PaymentCompleted`, `PaymentSessionCreated`, `PaymentSessionFailed`, `PaymentAmountMismatch`, `PaymentVerified`, `PaymentWebhookValidated` only. orders-svc (Phase 4) will consume these.
3. **Webhook ingress:** `/webhooks/stripe`, `/webhooks/paypal` — validate signature inline, publish `PaymentWebhookValidatedEvent` via outbox, return 200.
4. **gRPC API** (none for now — payments is asynchronous-only).
5. Same wiring as Phase 1.
6. **Webhook idempotency test:** replay the same Stripe webhook 3× → exactly one state transition (uses `MessageId == provider EventId` for inbox dedupe).

### Pre-req
Phase 1 done (JWT validation). Phase 2 done (catalog gRPC available — payments doesn't depend on catalog directly, but the saga later will need both).

### Test posture
- All payments-specific tests green.
- Webhook idempotency test green.
- Stripe E2E: hit `/webhooks/stripe` with a real-shaped payload → assert `PaymentCompletedEvent` published with correct fields.
- `pact-broker can-i-deploy --pacticipant payments-svc` passes.

### Done
- payments-svc deployed via ArgoCD, healthy.
- All payment-related Pacts published.

---

## Phase 4 — orders-svc (Week 6)

### What

1. Service code under `src/Orders/` cribbing from monolith's `src/Domain/Entities/Orders/`, `src/Application/Commands/Orders/`.
2. **Consumes:** `PaymentCompletedEvent` (transition Order to Paid), `PaymentSessionFailedEvent` + `OrderAbandonedEvent` (transition Order to Abandoned), `StockReservationFailedEvent` (transition Order to Abandoned).
3. **Publishes:** `OrderCreated`, `OrderCompleted`, `OrderAbandoned`.
4. **Read model:** `user_snapshots` table populated from `UserProfileChangedEvent` (consumer in `Haworks.BuildingBlocks.UserSnapshots`); `product_snapshots` table from `Catalog.ProductUpdatedEvent`.
5. **REST API** for order history queries.
6. **gRPC API** for the saga (Phase 5) to query order state.
7. Same wiring as Phase 1.

### Pre-req
Phases 1–3 done.

### Test posture
- All orders-specific tests green.
- Idempotency test: replay same `PaymentCompletedEvent` 3× → Order transitions to Paid exactly once (inbox dedupe).
- Cross-service E2E: create Order via REST → consume `PaymentCompletedEvent` → Order transitions to Paid → query via REST.
- `pact-broker can-i-deploy --pacticipant orders-svc` passes.

### Done
- orders-svc deployed via ArgoCD, healthy.

---

## Phase 5 — checkout-orchestrator-svc (Week 7)

**The crown jewel.** All upstream services exist; the saga ties them together.

### What

1. Service code under `src/CheckoutOrchestrator/`:
   - `CheckoutDbContext` containing `CheckoutSagaState` + per-context outbox/inbox tables (using `BoundedContextConsumerDefinition<T,TDb>` from BuildingBlocks).
   - `CheckoutSaga` MassTransit state machine, cribbed from monolith's `src/Infrastructure/Messaging/Sagas/CheckoutSaga.cs` but refactored to:
     - Use `Publish` (not `SendAsync` to hard-coded queues).
     - Correlate everything by `SagaId` (not `OrderId` for late events).
     - Add explicit timeout schedules (15-min payment expiry).
   - Compensation logic: `StockReservationFailed` → `OrderAbandoned`; `PaymentSessionFailed` / `PaymentExpired` → `ReleaseStock` + `OrderAbandoned`; `PaymentAmountMismatch` → mark Order `RequiresReview` + Slack alert.
   - `GetSagaStatus(orderId)` gRPC for ops/debug.
2. **No business state owned** — saga only holds correlation + last-known status.
3. Same wiring as Phase 1.
4. **The headline chaos test:** `tests/E2E/Chaos/SagaCompensationChaosTests.cs`:
   - Start a checkout via bff-web (or directly via orders-svc gRPC for now)
   - Wait for `OrderCreated`, `StockReserved` (poll with `TestWait.Until`)
   - `kubectl pause` payments-svc pod
   - Wait 90 s
   - Assert: saga in CheckoutOrchestrator transitioned to "Compensating"
   - Assert: catalog-svc received `StockReleaseRequested`
   - Assert: stock count back to pre-checkout level
   - Assert: Order is "Abandoned"
   - `kubectl unpause` payments-svc; run a fresh checkout end-to-end → green
5. **Record `make demo-saga-failure` as a 2-minute Loom.** This is the README hero video.

### Pre-req
Phases 1–4 done. All upstream events have published Pacts.

### Test posture
- Saga state machine tests: every transition, every compensation path, idempotent message redelivery.
- Saga restart test: kill checkout-orchestrator-svc mid-saga → restart → saga resumes from persisted state.
- Synthetic 1000-checkout test: zero zombie sagas after completion.
- The chaos test above.
- E2E: full happy-path checkout end-to-end through 5 services.
- `pact-broker can-i-deploy --pacticipant checkout-orchestrator-svc` passes.

### Done
- checkout-orchestrator-svc deployed via ArgoCD, healthy.
- Loom video recorded and linked from README.
- This is the moment the portfolio is "done enough" to share.

---

## Phase 6 — content-svc (Weeks 7–8)

### What

1. Service code under `src/Content/` cribbing from monolith's `src/Domain/Entities/Content/`, `src/Infrastructure/Storage/`, `src/Infrastructure/Scanning/` (ClamAV).
2. Upload sessions, file metadata, ClamAV scan orchestration, MinIO/S3 access.
3. Magic-byte signature check (per CLAUDE.md security mandate).
4. Same wiring as Phase 1. MinIO + ClamAV containers added to Aspire AppHost (already wired in Phase 0; service now references them).

### Pre-req
Phase 1 done (JWT validation).

### Test posture
- Upload + scan integration test (Testcontainers MinIO + ClamAV).
- Magic-byte rejection test (try uploading a JPG with `.exe` extension; assert rejected).
- E2E: upload → scan → metadata stored → retrieve.

### Done
- content-svc deployed via ArgoCD, healthy.

---

## Phase 7 — bff-web (Week 8)

### What

1. The public HTTP edge. CSRF (`AutoValidateAntiforgeryTokenAttribute`), controller mapping, SignalR hubs.
2. Composes services via gRPC clients: identity-svc, catalog-svc, orders-svc, payments-svc, content-svc.
3. Pushes checkout updates to user via SignalR (consumes `PaymentSessionCreatedEvent` from broker, pushes URL to connected clients).
4. Same wiring as Phase 1.

### Pre-req
All other services done.

### Test posture
- Cross-service E2E (Playwright): full user journey through bff-web → all backend services → success.
- All cross-system smoke tests green.

### Done
- bff-web deployed via ArgoCD, healthy.
- Public ingress configured.

---

## Phase 8 — Polish & Case Study (Week 8)

### What

1. **Write the case study** under `docs/case-study/` — one chapter per phase, with screenshots, code links, ADR references, lessons learned.
2. **Record demos:**
   - `make demo-saga-failure` (already in Phase 5).
   - `make demo-jwt-rotation` — identity-svc rotates RSA keypair; consumers refresh JWKS without restart.
   - `make demo-vault-rotation` — Vault rotates Postgres credentials; services pick up new creds via `DynamicCredentialsConnectionInterceptor`.
3. **Polish observability dashboards** — make sure the Grafana screenshots are publishable.
4. **README hero update** — diagram, links, demo video embeds.
5. **Optional public demo** — deploy to DigitalOcean managed Kubernetes (~$12/mo) so portfolio viewers get a live URL.
6. **Tag `v1.0`.**

### Pre-req
All previous phases done.

### Done
- README is recruiter-ready.
- Case study written.
- Demos recorded.

---

## Per-Phase Acceptance Criteria

Every phase ships when **all** of these are true:

1. All this service's unit + integration + contract + architecture tests green in CI.
2. `pact-broker can-i-deploy --pacticipant <svc> --to-environment production` returns true.
3. `dotnet run --project deploy/aspire` brings the service up cleanly with all health checks green.
4. `make k8s-up` deploys the service via ArgoCD, pod becomes ready within 60 s.
5. `scripts/local-deploy-smoke.sh` exercises the cross-service happy path through all services-so-far without errors.
6. README updated with a section for the new service.

No "soak periods." No phased rollouts. Phase done = merge to main.

---

## What If a Phase Goes Wrong

Greenfield means there's no rollback to plan — there's nothing to roll back to. If a phase doesn't ship cleanly:

1. **The PR doesn't merge.** Iterate until criteria are met.
2. **If the architecture is wrong** (e.g., the saga's event topology isn't right), revise the architecture doc, update the ADR, restart the phase.
3. **If a dependency is missing** (e.g., catalog needs an event from identity that wasn't built yet), add it to the prior phase's scope and re-merge.

The constraint is "all tests pass + local deploy works." That's the only halt signal.

---

## See also

- [01-architecture.md](./01-architecture.md) — what we're building
- [02-platform.md](./02-platform.md) — how it runs
- [04-testing-strategy.md](./04-testing-strategy.md) — how we know it works
- [05-risks.md](./05-risks.md) — what to watch for
- [adr/0008-clean-slate-greenfield.md](./adr/0008-clean-slate-greenfield.md) — why this approach
- [adr/0009-monolith-as-reference-not-source.md](./adr/0009-monolith-as-reference-not-source.md) — what the existing monolith is for
