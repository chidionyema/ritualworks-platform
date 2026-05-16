# Test Port — Gap Report

Snapshot of the monolith → platform test parity at the time the porting effort started. Companion to [`test-port-from-monolith.md`](./test-port-from-monolith.md). Use this to pick work from §9 of the brief.

> Date: 2026-05-04. Counts are approximate (file-grep level — `[Fact]`/`[Theory]` density). Updates expected as the port progresses; refresh by running `dotnet test --list-tests` for each project.

---

## Headline

| Suite | Monolith | Platform | Delta |
|---|---|---|---|
| Unit | ~1,120 | 157 | **-963 (14% ported)** |
| Integration | ~97 | 49 | **-48 (51% ported)** |
| Contract (Pact) | 1 | 11 | **+10 (platform ahead)** |
| Architecture | 13 | 27 | **+14 (per-context)** |
| Smoke | 4 | 0 | **-4 (whole suite missing)** |
| E2E (Playwright) | 1 | 0 | **-1 (whole suite missing)** |
| Performance | 0 (scaffold only) | 0 | tied |

The unit-test gap overstates real coverage debt — many monolith tests are repetitive `[Fact]` variants that fold into `[Theory]` rows. After fold, the realistic gap is ~400-500 net-new platform tests.

---

## Per-monolith-project breakdown (source side)

### `haworks.Tests.Unit/` — ~1,120 tests

- `Architecture/` — 1 file (cross-layer; **already exceeded** by per-context Architecture suites in platform — skip)
- `BackgroundServices/` — `StockJanitorServiceTests` (TBD if platform has equivalent)
- `Commands/`
  - `Auth/` — Register, Login, Logout, RefreshToken, LinkExternalLogin, UnlinkExternalLogin (~75 tests)
  - `Categories/` — Create, Update (21 tests)
  - `Checkout/` — `InitiateCheckoutCommand` (9 tests, **DO NOT PORT** — replaced by saga)
  - `Content/` — InitChunkSession, UploadChunk, CompleteChunkSession, DeleteContent (4 files)
  - `Products/` — Create, Update, Delete (13 tests)
  - `Reviews/` — Create, Approve, Update, Delete (4 files)
  - `Subscriptions/` — CreateSubscriptionCheckout (verify platform has subscriptions before porting)
  - `Users/` — SaveShippingInfo, UpdateUserProfile (small)
  - `Webhooks/` — ProcessPaymentWebhook
- `Controllers/` — 12 files (most already covered by platform `*.Integration` controller tests)
- `Domain/` — Order (22), Payment (24), Product (30), User (23) — **HIGH PRIORITY**, platform domain coverage is shallow
- `Payments/` — 7 files: WebhookIdempotencyGuard (12), PayPalPaymentProcessor (8, **SKIP**), StripeWebhookProcessor (13), PaymentGateway (8, **SKIP**), PaymentProviderHealthCheck (5), WebhookRouter (17, **SKIP**), PayPalWebhookProcessor (14, **SKIP**)
- `Queries/` — Auth, Category, Content, Order, Product, Review, User (7 files)
- `Services/` — JwtTokenService (24), RefreshTokenService (15), TokenRevocationService (18), UserEmailService (19), VaultService (4) — **HIGH PRIORITY** for security/infra
- `Validators/` — 7 files, **176 tests total** — biggest single bucket
- `Consumers/` — CheckoutNotification, OrderCompleted, PaymentSession, PaymentVerified (4 files, 43 tests)

### `haworks.Tests.integration/` — ~97 tests

- `Chaos/CheckoutChaosTests` (1 test) — partially superseded by `SagaCompensationChaosTests`
- `Consumers/` — 6 files, 57 tests:
  - `PaymentCompletedConsumerIntegrationTests` (15)
  - `PaymentSessionConsumerIntegrationTests` (13)
  - `StockReservationConsumerIntegrationTests` (9)
  - `StockReleaseConsumerIntegrationTests` (8)
  - `CheckoutSagaIntegrationTests` (8)
  - `PaymentWebhookConsumerIntegrationTests` (4)
- `Controllers/` — 7 files (Auth, ExternalAuth, Category, Products, Content, Checkout, Orders, Webhooks, UserProfile)
- `Observability/TracePropagationTests` (1)
- `Outbox/` — 2 files, 7 tests (`OutboxWiringIntegrationTests`, `OutboxInboxAutomaticBehaviorProbe`)
- `Regression/` — `MigrationsCompiledRegressionTests` (2), `MultiContextOutboxLockSqlTests` (2), `PendingModelChangesRegressionTests` (1), `VaultProvisioningContractTests` (3)
- `Saga/CheckoutSagaEndToEndTests` (2) — verify platform's saga integration covers same scenarios
- `Vault/` — 4 files, 7 tests (DynamicCredentials, RotationUnderLoad, RotationConcurrentSoak, WebhookRealSignature)

### `haworks.Tests.Smoke/` — 4 tests

- `ApiHealthSmokeTests` (3) — root, /health, swagger
- `PaymentConnectivitySmokeTests` (1) — payment provider /health
- Plus `EnvironmentAgnosticFixture.cs` for `TARGET_URL` switch

### `haworks.Tests.E2E/CheckoutE2ETests` — 1 Playwright test

Mocks Stripe via WireMock; happy + failure path.

### `haworks.Tests.Architecture/` — 13 tests

`BoundedContextBoundaryTests.cs` — superseded by per-context `*.Architecture/` projects in the platform.

### `haworks.Tests.Contract/` — 1 test

`Catalog/CatalogToPaymentsContractTests.cs` — superseded by 11 contract tests across 4 platform contexts.

### `haworks.Tests.Performance/` — 0 active

Scaffold only. Not blocking.

---

## Coverage map by domain concern

Use this when picking a test to port — find the row by concern, see what's missing.

| Concern | Monolith location | Platform location | State |
|---|---|---|---|
| User registration | `Unit/Commands/Auth/RegisterCommandHandlerTests` (14) | `Identity.Unit/Commands/Auth/RegisterCommandHandlerTests` (1) | partial |
| Login (password) | `Unit/Commands/Auth/LoginCommandHandlerTests` (13) | `Identity.Unit/Commands/Auth/LoginCommandHandlerTests` (2) | partial |
| External OAuth | `Unit/Commands/Auth/{Link,Unlink}ExternalLogin*` + `Controllers/ExternalAuthenticationControllerTests` | `Identity.Integration/AuthFlowsTests` + `RoundedOutAuthFlowsTests` | partial (integration only) |
| Token refresh | `Unit/Commands/Auth/RefreshTokenCommandHandlerTests` (17) + `Services/RefreshTokenServiceTests` (15) | `Identity.Unit/JwtTokenServiceTests` (4) | **MISSING** — Tier 1 |
| JTI revocation | `Unit/Services/TokenRevocationServiceTests` (18) | none | **MISSING** — Tier 1 (security-critical) |
| JWT issuance | `Unit/Services/JwtTokenServiceTests` (24) | `Identity.Unit/JwtTokenServiceTests` (4) | partial |
| Profile / shipping | `Unit/Commands/Users/*` + `Controllers/UserProfileControllerTests` | `Identity.Unit/Commands/Users/*` (2 ea) + `Controllers/UserProfileControllerTests` | partial |
| Product CRUD | `Unit/Commands/Products/*` (13) + `Controllers/ProductsControllerTests` + `Domain/ProductTests` (30) | `Catalog.Unit/Commands/Products/{Update,Delete}` (2) + `ProductTests` (1) | **MINIMAL** |
| Categories | `Unit/Commands/Categories/{Create,Update}` (21) + `Controllers/CategoryControllerTests` + `Validators/CategoryValidatorTests` | none | **MISSING** — wholesale |
| Reviews | `Unit/Commands/Reviews/*` (4 files) + `Controllers/ProductReviewsControllerTests` + `Queries/ReviewQueryHandlerTests` | none | **MISSING** — wholesale |
| Stock reservation | `integration/Consumers/StockReservationConsumerIntegrationTests` (9) + `StockReleaseConsumerIntegrationTests` (8) | `Catalog.Contract/StockReservedConsumerTests` (1) + implicit in saga flows | partial |
| Checkout flow | `Unit/Commands/Checkout/InitiateCheckoutCommandHandlerTests` (9, **SKIP**) + `integration/Saga/CheckoutSagaEndToEndTests` (2) + `integration/Chaos/CheckoutChaosTests` (1) | `CheckoutOrchestrator.Integration/{SagaFlowsTests, SagaCompensationChaosTests}` | platform improved |
| Stripe webhook signature | `Unit/Payments/StripeWebhookProcessorTests` (13) + `integration/Vault/WebhookRealSignatureTests` (2) | `Payments.Integration/WebhookFlowsTests` (7) + `Payments.Unit/StripeSignatureValidatorTests` (1) | partial |
| Webhook idempotency | `Unit/Payments/WebhookIdempotencyGuardTests` (12) + integration | `Payments.Integration/WebhookFlowsTests` (one scenario in 7) | partial — Tier 1 |
| Payment gateway | `Unit/Payments/PaymentGatewayTests` (8) + `WebhookRouterTests` (17) | none | **SKIP — design changed** |
| Payment health | `Unit/Payments/PaymentProviderHealthCheckTests` (5) | implicit /health | gap |
| PayPal | `Unit/Payments/PayPal*` (22) | none | **SKIP — feature removed** |
| Order state | `Unit/Domain/OrderTests` (22) | `Orders.Unit/OrderTests` (1) | **MINIMAL** |
| Order queries | `Unit/Queries/OrderQueryHandlerTests` + `integration/Controllers/OrdersControllerTests` | `Orders.Unit/Queries/*` + Controllers | partial |
| Content chunked upload | `Unit/Commands/Content/{Init,Upload,Complete}*` (4) + `Controllers/ContentControllerTests` + `Validators/ContentValidatorTests` (22) | `Content.Unit/Commands/*` (2) + `Content.Integration/Controllers/*` (2 + 10 skipped) | partial — blocked on `IFileValidator` + `IChunkedUploadService` impls (see brief §8.4) |
| Content delete | `Unit/Commands/Content/DeleteContentCommandHandlerTests` | `Content.Unit/Commands/DeleteContentCommandHandlerTests` | **FULL** |
| Email | `Unit/Services/UserEmailServiceTests` (19) | none | **MISSING** — Tier 4 |
| Vault | `Unit/Services/VaultServiceTests` (4) + `integration/Vault/*` (7) | none | **MISSING** — Tier 2 |
| Outbox | `integration/Outbox/*` (7) | none | **MISSING** — Tier 1.5 |
| Cross-context locks | `integration/Regression/MultiContextOutboxLockSqlTests` (2) | none | **SKIP** — by ADR-0004 there's no cross-context schema in platform |
| Migrations | `integration/Regression/MigrationsCompiledRegressionTests` (2) | none | gap — Tier 4 |
| Tracing | `integration/Observability/TracePropagationTests` (1) | none | gap — Tier 4 |
| Architecture | `Architecture/BoundedContextBoundaryTests` (13) | per-context Architecture (27) | **EXCEEDED** |
| Pact | `Contract/Catalog/CatalogToPaymentsContractTests` (1) | 7 files across 4 contexts (11 tests) | **EXCEEDED** |
| Smoke | `Smoke/*` (4) | none | **MISSING** — Tier 5 |
| E2E | `E2E/CheckoutE2ETests` (1) | none | **MISSING** — Tier 5 |

---

## Suggested batching

Each row below = one PR-sized chunk.

1. **PR `port/test-token-revocation`** — `TokenRevocationServiceTests` to `Identity.Unit/Services/`
2. **PR `port/test-refresh-token`** — `RefreshTokenServiceTests` to `Identity.Unit/Services/`
3. **PR `port/test-webhook-idempotency`** — webhook idempotency guard → `Payments.Unit/`
4. **PR `port/test-validators-auth`** — `AuthValidatorTests` split per command → `Identity.Unit/Validators/`
5. **PR `port/test-validators-user`** — same shape → `Identity.Unit/Validators/`
6. **PR `port/test-validators-product`** — → `Catalog.Unit/Validators/`
7. **PR `port/test-validators-category`** — → `Catalog.Unit/Validators/`
8. **PR `port/test-jwt-token-service`** — fill the 20-test gap → `Identity.Unit/`
9. **PR `port/test-outbox-dedup`** — outbox/inbox tests → `BuildingBlocks.Testing.Integration/` (new project)
10. **PR `port/test-domain-order`** — `OrderTests` (22) → `Orders.Unit/Domain/`
11. **PR `port/test-domain-payment`** — `PaymentTests` (24) → `Payments.Unit/Domain/`
12. **PR `port/test-domain-product`** — `ProductTests` (30) → `Catalog.Unit/Domain/`
13. **PR `port/test-domain-user`** — `UserTests` (23) → `Identity.Unit/Domain/`
14. **PR `port/test-categories-crud`** — Create + Update + queries + controller → `Catalog.Unit/`
15. **PR `port/test-reviews-crud`** — 4 review handlers + queries → `Catalog.Unit/`
16. **PR `port/test-vault-rotation`** — Vault integration tests → `BuildingBlocks.Testing.Integration/`
17. **PR `feat/test-smoke-suite`** — net-new `tests/Smoke/` project + 4 tests
18. **PR `feat/test-e2e-suite`** — net-new `tests/E2E/` project + Playwright checkout

Stop after #3 and re-evaluate priorities — security-critical work first lets us ship the next platform release with confidence even if domain coverage stays thin for another sprint.

---

## Refresh procedure

When this report goes stale (a few PRs in), regenerate the headline counts:

```bash
cd /Users/chidionyema/Documents/code/haworks-platform
for proj in tests/*.Unit/*.csproj tests/*.Integration/*.csproj tests/*.Architecture/*.csproj tests/*.Contract/*.csproj; do
  count=$(dotnet test "$proj" --list-tests --no-build 2>/dev/null | grep -c '^    ')
  echo "$count  $proj"
done
```

Update the headline table + the per-context breakdown.
