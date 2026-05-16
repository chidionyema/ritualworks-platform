# Haworks Platform: Staff-Level Audit & Remediation Plan

## PART 1: Staff-Level Code Review & Architectural Analysis

### 1. Platform-Wide Architecture & Patterns
**Strengths:**
* **Strict CQRS & MediatR:** Controllers are properly designed as thin wrappers. The `Result<T>` pattern correctly prevents using exceptions for control flow.
* **Resiliency & Consistency:** The mandate for the Outbox pattern guarantees that database state and MassTransit events are committed atomically, preventing split-brain scenarios between microservices.
* **Security Posture (CI/CD):** The automated scanning pipeline is world-class. Integrating CodeQL, ZAP, Nuclei, and AI-driven exploratory testing into the deployment lifecycle is a highly mature pattern.

**Systemic Weaknesses:**
* **The "Validation Gap":** While `ValidationBehavior` is present, compliance is not 100%. Many critical commands lack `AbstractValidator<T>` implementations, meaning requests reach the domain layer with malformed or malicious data.
* **Missing API Gateway/BFF Guardrails:** The `BffWeb` is acting as a pass-through but lacks fundamental platform protections like global rate-limiting. This exposes downstream microservices to DDoS, credential stuffing, and scraping.
* **IDOR (Insecure Direct Object Reference) Epidemic:** Across multiple services, user identity is being trusted from the request body (e.g., `body.UserId`) rather than being strictly extracted from the JWT Claims (`HttpContext.GetForwardedUserId()`).

### 2. Service-by-Service Review

**Payments & Payouts**
* **Critique:** Financial ledgers and state machines lack pessimistic concurrency controls and strict invariants.
* **Actionable Feedback:** The `Payment` entity must enforce state transitions natively. Currently, it allows transitioning to `Refunded` even if the payment isn't `Completed`. The `LedgerAccount` in Payouts lacks database-level constraints to prevent negative balances. Disbursement logic must be wrapped in transactions with concurrency tokens to prevent race conditions during payout sweeps.

**CheckoutOrchestrator & Orders**
* **Critique:** The Checkout Saga handles the happy path well but fails open on edge cases.
* **Actionable Feedback:** The saga state machine does not enforce a unique index on `OrderId` at the database level, allowing race conditions to create duplicate checkout flows. Furthermore, if a `PaymentAmountMismatch` occurs, the saga fails to publish a `StockReleaseRequestedEvent`, resulting in permanently locked catalog inventory.

**Identity**
* **Critique:** Authentication is functional but lacks enterprise hardening.
* **Actionable Feedback:** `JwtTokenService` issues tokens but lacks a synchronous revocation check during `ValidateToken`. Logged-out or banned users retain access until the JWT naturally expires. `ExternalAuthenticationController` is vulnerable to Open Redirects because it does not validate that relative redirect URLs start with `/` and contain no path traversals (`..`).

**BffWeb & Content**
* **Critique:** Security boundaries are porous at the edge.
* **Actionable Feedback:** The `ContentController` accepts file uploads without verifying MIME types against an explicit allowlist, opening the platform to RCE via malicious file uploads. `CheckoutController` and `LocationsController` are entirely missing `[Authorize]` attributes, exposing internal logic to anonymous traffic.

**Catalog & Location**
* **Critique:** Lacking sensible defaults and bounds checking.
* **Actionable Feedback:** `GetNearbyAddressesQuery` in the Location service accepts arbitrary radius sizes. A malicious user can pass a 50,000km radius, causing a full table scan and degrading the PostGIS database. Catalog mutations (Create/Update Product) lack authorization checks, allowing anonymous users to modify the store.

**Infrastructure & Deployments**
* **Critique:** Incomplete platform rollout.
* **Actionable Feedback:** Six services (Analytics, FeatureFlags, Media, Realtime, RulesEngine, Localization) contain code but lack `fly.toml` configurations—meaning they are completely undeployed. Furthermore, while OpenTelemetry is wired into the code, the actual infrastructure to ingest it (Prometheus/Grafana/Loki) does not exist, leaving the system effectively blind in production.

---

## PART 2: Bug Hunt, Edge Cases, & Gaps Report

### 🔴 High-Severity Bugs & Exploits
1. **Webhooks SSRF (Server-Side Request Forgery):**
    * **The Bug:** The Webhooks service allows users to subscribe to events by providing a URL. There is no validation to prevent requests to private/internal IPs (e.g., 10.x.x.x, 127.0.0.1, 169.254.169.254).
    * **The Exploit:** An attacker can use the webhook service to port-scan the internal cluster network or extract AWS/Fly instance metadata credentials.
2. **Order Ownership IDOR:**
    * **The Bug:** `OrdersController.Get(Guid id)` does not verify that the authenticated `userId` matches the `Order.UserId`.
    * **The Exploit:** Any authenticated user can enumerate order IDs and view other customers' PII, shipping addresses, and purchase histories.
3. **Arbitrary File Upload in Content Service:**
    * **The Bug:** `FileSignatureValidator` does not reject unknown/executable MIME types.
    * **The Exploit:** Uploading an `.exe`, `.sh`, or malicious SVG could lead to XSS or remote execution if served from the same origin.

### 🟡 Edge Cases & State Machine Failures
1. **Orphaned Stock Reservations:**
    * **Scenario:** A user initiates checkout (stock is reserved). The payment succeeds but the amount is mismatched (e.g., price changed mid-checkout). The saga transitions to `RequiresReview`, but fails to release the reserved stock.
2. **Double Refund Exploit / Over-refunding:**
    * **Scenario:** `CreateRefundCommand` does not validate that the sum of existing refunds plus the new refund is `<= payment.Amount`. A merchant could accidentally or maliciously trigger a refund for $200 on a $100 order.
3. **Negative Payout Sweeps:**
    * **Scenario:** A seller receives a $50 payment, then is hit with a $100 chargeback. The Ledger drops to -$50. The Disbursement service attempts a payout without a positive-balance guard, potentially throwing errors or communicating invalid states to the payment gateway.
4. **Saga Race Conditions:**
    * **Scenario:** A user double-clicks the "Start Checkout" button. Because there is no unique index on `CheckoutSagaState.OrderId`, two parallel sagas are instantiated for the same order, potentially double-charging the user.

### 🔵 Platform Gaps & Missing Features
1. **Global Rate Limiting (Missing):** BffWeb has no throttling. The platform is highly susceptible to volumetric DDoS attacks.
2. **Pricing & Tax Engines (Missing):** The `src/Pricing/` directory is an empty shell with a README. The platform currently has no way to calculate dynamic pricing, tiered pricing, discounts, or apply geographic taxes (Avalara/TaxJar).
3. **Observability Blackhole:** Tempo is deployed for tracing, but there is no Prometheus instance scraping the endpoints, and no Loki instance aggregating logs. The `saga-alerts.yml` file exists but has no Alertmanager to route the alerts.
4. **Secret Rotation:** Stripe API keys, S3 keys, and JWT signing keys are set as static Fly secrets and never rotated, violating compliance standards for financial platforms.
5. **Disaster Recovery:** The platform relies heavily on Vault and Neon Postgres, but there are no automated backup tests, `pg_dump` verifications, or PITR (Point-In-Time-Recovery) runbooks. Data loss in the Vault database would permanently sever access to all encrypted tenant data.

---

## PART 3: Comprehensive Remediation Plan

This section provides the actionable blueprint to systematically resolve every issue identified in Parts 1 and 2.

### Remediation Strategy for Architectural & Systemic Weaknesses
1. **The "Validation Gap"**
   - **Plan:** Execute a search across all `src/**/*.cs` for MediatR `IRequest`, `ICommand`, and `IQuery` implementations. Cross-reference them with existing `AbstractValidator<T>` definitions. Create a tracking issue to implement all missing validators. Introduce a Roslyn Analyzer or an Architecture Test in `Platform.ArchitecturalGuards` to automatically fail the CI build if any MediatR request lacks a corresponding validator.
2. **Missing API Gateway/BFF Guardrails**
   - **Plan:** Introduce `AddPlatformRateLimiting()` in `BuildingBlocks/Extensions`. Apply Fixed Window and Token Bucket rate limiters to `BffWeb` inside `Program.cs`. Ensure strict HTTP 429 Too Many Requests responses are returned for excessive traffic.
3. **IDOR (Insecure Direct Object Reference) Epidemic**
   - **Plan:** Audit all controllers and MediatR handlers that accept a `UserId` as part of the request payload. Refactor these to exclusively extract the identity from the authenticated JWT claims using `HttpContext.User`. Remove `UserId` properties from DTOs where it relies on client input.

### Remediation Strategy for Service-by-Service Feedback
1. **Payments & Payouts**
   - **Plan:** Update the `Payment` domain entity to encapsulate and rigorously enforce state transitions (e.g., throwing a `DomainException` or returning a `Result.Failure` if transitioning to `Refunded` when not `Completed`). Apply a database-level `CHECK (Balance >= 0)` constraint to `LedgerAccount` via EF Core migrations. Wrap payout sweep logic in an `IDbContextTransaction` and utilize EF Core concurrency tokens (`[ConcurrencyCheck]`).
2. **CheckoutOrchestrator & Orders**
   - **Plan:** Generate an EF Core migration in `CheckoutOrchestrator` to enforce a unique index on `CheckoutSagaState.OrderId`. Modify the MassTransit Saga state machine to explicitly publish a `StockReleaseRequestedEvent` during the `PaymentAmountMismatch` compensation block.
3. **Identity**
   - **Plan:** Implement a high-performance revocation check (e.g., using Redis caching) inside `JwtTokenService.ValidateToken` to immediately invalidate sessions for banned or logged-out users. Update `ExternalAuthenticationController` to strictly validate redirects using `Url.IsLocalUrl()` and strip directory traversal sequences (`..`).
4. **BffWeb & Content**
   - **Plan:** Universally apply the `[Authorize]` attribute to `CheckoutController` and `LocationsController`. Rewrite the `FileSignatureValidator` in the Content service to verify file streams against an explicit, hardcoded allowlist of known-safe magic bytes (e.g., PNG, JPEG, PDF) before saving files to storage.
5. **Catalog & Location**
   - **Plan:** Add `[Authorize(Roles = "Admin")]` to Catalog endpoints modifying data (Create/Update). Enforce a hard maximum radius limit (e.g., 50km) on the `GetNearbyAddressesQuery` in the Location service, returning an HTTP 400 Bad Request for any value exceeding this threshold.
6. **Infrastructure & Deployments**
   - **Plan:** Scaffold `fly.toml` files and Dockerfiles for `Analytics`, `FeatureFlags`, `Media`, `Realtime`, `RulesEngine`, and `Localization`. Update the primary deployment scripts (`deploy.yml` and `deploy.sh`) to include these services. Instantiate Prometheus and Loki configuration files within `infra/observability/` and hook them up to the existing OpenTelemetry collectors.

### Remediation Strategy for High-Severity Bugs & Exploits
1. **Webhooks SSRF (Server-Side Request Forgery)**
   - **Plan:** Implement an `IsPublicIpAddress` check in the Webhook validation pipeline. Resolve the destination URL to an IP address before dispatching, and explicitly reject RFC 1918 private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), loopback (`127.0.0.0/8`), and link-local (`169.254.0.0/16`) addresses to prevent internal network scanning and metadata extraction.
2. **Order Ownership IDOR**
   - **Plan:** Modify the MediatR query `GetOrderByIdQuery` to include the caller's `UserId`. Update the corresponding handler to enforce an ownership check: `if (order.UserId != request.UserId) return Result.Forbidden()`.
3. **Arbitrary File Upload in Content Service**
   - **Plan:** Restrict the MIME-type parsing in `FileSignatureValidator` to prevent executable or script-based extensions (`.exe`, `.sh`, `.php`, `.svg` containing scripts) by ensuring both the file extension and the internal file header (magic number) conform strictly to a predefined safe-list.

### Remediation Strategy for Edge Cases & State Machine Failures
1. **Orphaned Stock Reservations**
   - **Plan:** Inject a compensation step inside the Checkout Saga definition. When `PaymentAmountMismatch` is triggered and transitions to `RequiresReview`, emit a `StockReleaseRequestedEvent` to the Catalog service to clear the inventory hold.
2. **Double Refund Exploit / Over-refunding**
   - **Plan:** Inside `CreateRefundCommandHandler`, fetch the `Payment` record and all its associated `Refund` records. Compute the total sum of prior refunds plus the newly requested refund amount. Reject the operation if `TotalRefunds > Payment.Amount`.
3. **Negative Payout Sweeps**
   - **Plan:** Implement defensive logic inside `DisbursementService` to select the `LedgerAccount` with a pessimistic lock. Add domain validation ensuring the sweep amount is `> 0` and does not exceed the current balance. Rely on the newly added DB `CHECK` constraint as a last line of defense.
4. **Saga Race Conditions**
   - **Plan:** Add an EF Core `HasIndex(x => x.OrderId).IsUnique()` configuration to `CheckoutSagaState`. Let database uniqueness constraints throw an exception on concurrent saga initializations, and configure MassTransit to handle/swallow the concurrency exception gracefully.

### Remediation Strategy for Platform Gaps & Missing Features
1. **Global Rate Limiting (Missing)**
   - **Plan:** Execute Backlog Item B-004. Integrate `Microsoft.AspNetCore.RateLimiting` in `BffWeb`, defining partitions for authenticated vs. unauthenticated users.
2. **Pricing & Tax Engines (Missing)**
   - **Plan:** Execute Backlog Items B-003 and B-006. Scaffold the `Pricing` and `Tax` domain projects. Define `CalculateEffectivePriceQuery` and plug it into the `CheckoutOrchestrator` flow prior to payment execution.
3. **Observability Blackhole**
   - **Plan:** Execute Backlog Item B-001. Finalize the configurations for `infra/observability/prometheus/prometheus.yml` and `infra/observability/loki/loki.yaml`. Deploy them via docker-compose and integrate Alertmanager with the existing `saga-alerts.yml` rules.
4. **Secret Rotation**
   - **Plan:** Execute Backlog Item B-008. Migrate static secrets from Fly environment variables into HashiCorp Vault. Implement an automated worker in the `Scheduler` service to poll for expiring secrets and trigger rotation APIs (like Stripe key rolling).
5. **Disaster Recovery**
   - **Plan:** Execute Backlog Item B-005. Write `docs/DR-RUNBOOK.md` mapping out Point-In-Time-Recovery (PITR) procedures. Establish a CI/CD job using GitHub Actions that periodically tests the restoration of the Postgres databases from backup to verify data integrity.
