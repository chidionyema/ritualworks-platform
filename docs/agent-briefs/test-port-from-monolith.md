# Agent Brief — Port Tests from `haworks` (Monolith) to `haworks-platform` (Microservices)

**Audience:** an autonomous coding agent (Gemini, Claude, etc.) that has shell + read/write access to both repos and can run `dotnet`/`docker`.
**Goal:** close the test-coverage gap between the monolith and the new microservices platform, in priority order.
**Definition of done per task:** the ported test compiles, runs green in its target test project, and does not reference any type that lives only in the monolith.

This brief is **self-contained** — you do not need access to any prior chat to follow it. Read it top to bottom before starting.

---

## 1. The two repositories

### Source (read-only reference)

- Path: `/Users/chidionyema/Documents/code/haworks/`
- This is the .NET 9 modular monolith. Tests live under `tests/`:
  - `haworks.Tests.Unit/`
  - `haworks.Tests.integration/` (note lowercase `integration`)
  - `haworks.Tests.Architecture/`
  - `haworks.Tests.Contract/`
  - `haworks.Tests.Smoke/`
  - `haworks.Tests.E2E/`
  - `haworks.Tests.Performance/`
- **Do not modify anything in this repo.** It is the source of truth for what behaviour we want to keep covered, but its code is the wrong shape for the new platform.

### Target (where ported tests land)

- Path: `/Users/chidionyema/Documents/code/haworks-platform/`
- This is the .NET 9 microservices platform — 7 bounded contexts, per-service Postgres, MassTransit + RabbitMQ, EF outbox per service.
- Test projects live under `tests/`:
  - `Catalog.{Unit,Architecture,Contract,Integration}/`
  - `Identity.{Unit,Architecture,Contract,Integration}/`
  - `Orders.{Unit,Architecture,Contract,Integration}/`
  - `Payments.{Unit,Architecture,Contract,Integration}/`
  - `Content.{Unit,Architecture,Contract,Integration}/`
  - `BffWeb.{Architecture,Integration}/` (no `.Unit` or `.Contract` yet — create them only if a target test demands it)
  - `CheckoutOrchestrator.{Architecture,Integration}/`
- Shared test infrastructure: `src/BuildingBlocks.Testing/` — read this before writing any test fixture.

The platform is at v1.0.0 (tag exists). Branch off `main`.

---

## 2. Where things live in the platform

```
src/
├── BuildingBlocks/                # cross-cutting infra; no domain types
│   ├── Common/                      Result<T>, Error, Guards
│   ├── Behaviors/                   MediatR pipeline (Validation, Logging)
│   ├── Persistence/                 EF outbox, dynamic-creds interceptor
│   ├── Messaging/                   IDomainEventPublisher, MT outbox conventions
│   ├── Resilience/                  Polly policies, ResilienceOptions presets
│   ├── Vault/                       AppRole authenticator, VaultConfigBootstrap
│   ├── CurrentUser/                 ICurrentUserService + impl
│   └── ...
├── BuildingBlocks.Testing/        # SHARED test helpers — use first
│   ├── TestModuleInitializer.cs     Docker socket fix for macOS Testcontainers
│   └── Authentication/
│       ├── TestAuthenticationHandler.cs   shared no-op auth scheme
│       └── TestAuthMiddleware.cs
├── Contracts/                     # event payloads typed per publishing context
│   ├── Catalog/                     StockReservedEvent, StockReleasedEvent, ...
│   ├── Orders/                      OrderCreatedEvent, ...
│   └── Payments/                    PaymentCompletedEvent, PaymentSessionFailedEvent, ...
├── Catalog/
│   ├── Catalog.Domain/              entities, value objects, domain events
│   ├── Catalog.Application/         Commands/, Queries/, Validators/, Interfaces/, DependencyInjection.cs
│   ├── Catalog.Infrastructure/      EF persistence, MassTransit consumers, DI extensions
│   └── Catalog.Api/                 controllers + Program.cs
├── Identity/, Orders/, Payments/, Content/, BffWeb/, CheckoutOrchestrator/   # same shape
```

**Rule of thumb:** the platform mirrors the monolith *conceptually* but never *physically*. A monolith handler under `src/Application/Commands/Auth/RegisterCommandHandler.cs` lives at `src/Identity/Identity.Application/Commands/Auth/RegisterCommandHandler.cs` in the platform. Use the corresponding test project (`tests/Identity.Unit/`).

If you can't find the platform handler that corresponds to a monolith handler, that handler **may not exist yet** — see §8 (escalation).

---

## 3. Mandatory conventions — read before writing any test

These are enforced by the project's `.claude/rules/` and `Directory.Build.props`. Violating them breaks the build (`TreatWarningsAsErrors=true`).

### Code style
- File-scoped namespaces: `namespace Haworks.Catalog.Unit.Commands;`
- Nullable reference types are on; treat `string?` and `string` as different.
- `internal sealed class` for handlers, validators, consumers, DTOs.
- `async` methods end with `Async`.
- Private fields: `_camelCase`. Constants: `PascalCase`. No magic strings — see `.claude/rules/code-quality.md` for the constants taxonomy.
- Use primary constructors for DI: `internal sealed class Foo(IBar bar) : ...`.

### Test style
- xUnit, FluentAssertions, Moq.
- `Should()` syntax, never raw `Assert.Equal`.
- Mock async setups always pass `It.IsAny<CancellationToken>()`.
- Test method names: `Method_Scenario_ExpectedOutcome` (e.g. `Handle_InvalidEmail_ReturnsFailure`).
- Tag suites with `[Trait("Category", "Integration")]` for integration, `[Trait("Category", "Chaos")]` for chaos.
- **No `Task.Delay` for synchronisation.** Use polling (`PollUntilAsync`) with a deadline. Hard-coded sleeps are blocked by code review.
- Builder pattern for non-trivial test data. See `tests/Catalog.Unit/Helpers/` for the convention.
- For integration test fixtures: copy the shape of `tests/Orders.Integration/OrdersWebAppFactory.cs`. It already wires Testcontainers Postgres + the shared `TestAuthenticationHandler`.

### Patterns the platform uses (and the monolith may not)

| Concern | Platform pattern | Monolith pattern (don't carry over) |
|---|---|---|
| Returning errors from a handler | `Result<T>` + `Result.Failure(Error.X)` | sometimes throws domain exceptions |
| Cross-context state changes | publish an event via `IDomainEventPublisher` BEFORE `SaveChangesAsync` | direct `IOrderRepository` calls from `StripePaymentProcessor` |
| Idempotency | DB-level (unique index) + MT inbox dedup | sometimes hand-rolled `IIdempotencyKeyService` |
| Webhook processing | one consumer per provider, handler stays in Application layer | `WebhookRouter` + provider-specific processors |
| Auth | identity-svc issues JWT + JTI; other services validate via JWKS | shared L1/L2 cache in monolith |
| Saga | `CheckoutOrchestrator` MassTransit state machine | `ProcessCheckoutCommandHandler` "god handler" |

### Test infrastructure — already built, reuse it

- **Testcontainers on macOS:** the `[ModuleInitializer]` in `src/BuildingBlocks.Testing/TestModuleInitializer.cs` is `<Compile Include>`-linked into every integration test project. It fixes the Docker socket discovery + a Testcontainers regex catastrophic-backtracking bug. **You don't need to do anything — it just works** as long as your test project links it (every existing `*.Integration.csproj` already does).
- **Test auth scheme:** `TestAuthenticationHandler.SchemeName` is `"Test"`. Wire it in your fixture's `ConfigureServices` with `services.AddAuthentication(TestAuthenticationHandler.SchemeName).AddTestAuth();`. The handler grants a permissive principal (User + Admin + ContentUploader roles) so any `[Authorize]` policy passes.
- **MassTransit harness:** integration tests grafting in-memory MassTransit follow the pattern in `tests/Orders.Integration/OrdersWebAppFactory.cs` (look for `AddMassTransitTestHarness`). Production code skips its real `AddMassTransit` when `ASPNETCORE_ENVIRONMENT=Test`.

### Per-test-class shape (canonical example)

```csharp
using FluentAssertions;
using Haworks.BuildingBlocks.Common;
using Haworks.Identity.Application.Commands.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Haworks.Identity.Unit.Commands.Auth;

public sealed class LoginCommandHandlerTests
{
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly Mock<IJwtTokenService> _tokens = new();
    private readonly LoginCommandHandler _sut;

    public LoginCommandHandlerTests()
    {
        _sut = new LoginCommandHandler(
            _users.Object, _hasher.Object, _tokens.Object,
            NullLogger<LoginCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_UnknownEmail_ReturnsFailure()
    {
        _users.Setup(u => u.FindByEmailAsync("nobody@example.com", It.IsAny<CancellationToken>()))
              .ReturnsAsync((User?)null);

        var result = await _sut.Handle(
            new LoginCommand("nobody@example.com", "pw"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(Error.Identity.UnknownAccount);
    }
}
```

If any of these conventions clash with the monolith test you're porting, **the platform convention wins**. Rewrite the test idiomatically for the platform; do not import monolith helpers.

---

## 4. The porting rule book

When transforming a monolith test into a platform test:

1. **Map by behaviour, not by line.** A monolith test that asserts "registering with a duplicate email returns 409" maps to "the platform's `RegisterCommandHandler` returns `Result.Failure(Error.Identity.DuplicateEmail)`". Don't blindly copy assertions; restate them in platform terms (Result pattern, etc.).
2. **Fold parameterised duplicates.** If the monolith has 5 separate `[Fact]`s that test the same handler with different invalid inputs, fold them to one `[Theory]` with `[InlineData]` rows.
3. **Preserve the test name's intent.** If the monolith calls it `Login_WrongPassword_DoesNotIssueToken`, keep the same triple — but adapt to platform `Method_Scenario_ExpectedOutcome` style if it isn't already.
4. **Drop tests for features the platform doesn't have.** PayPal processor tests, the god-handler `InitiateCheckoutCommandHandler` tests, the modular monolith's cross-context join tests — skip these. See §6 for the exclusion list.
5. **Don't port mock setups for dependencies the platform handler doesn't take.** Many platform handlers have fewer dependencies (the saga absorbed them). Mock only what the platform handler asks for in its constructor.
6. **Use existing platform error names.** Look in `src/BuildingBlocks/Common/Error.cs` for the canonical error catalog. If the monolith asserts on a string literal `"User not found"`, find the equivalent `Error.Identity.UnknownAccount` in the platform.
7. **Integration tests must use Testcontainers, never InMemoryDatabase.** Per `.claude/rules/testing.md`. Adapt the fixture from `tests/Orders.Integration/OrdersWebAppFactory.cs`.
8. **Never add `Task.Delay` for synchronisation.** If the monolith has it, replace with `PollUntilAsync(predicate, TimeSpan.FromSeconds(30))`. There's a reference implementation at `tests/Payments.Integration/WebhookFlowsTests.cs:232`.

### Worked example — port `JwtTokenServiceTests` slice

**Monolith source:** `/Users/chidionyema/Documents/code/haworks/tests/haworks.Tests.Unit/Services/JwtTokenServiceTests.cs`

The monolith file has 24 tests. The platform already has `tests/Identity.Unit/JwtTokenServiceTests.cs` with 4 tests covering core signing. Your job is to fill the gap.

For each monolith test:
1. Read the test method.
2. Check whether the same scenario is already in the platform file. If yes, skip.
3. If no:
   - Identify which platform `IJwtTokenService` / `JwtTokenService` API it exercises (read `src/Identity/Identity.Application/...`).
   - Write the platform-shaped equivalent.
   - If the API doesn't exist, **stop and escalate** (§8) — do not invent platform APIs.

**Acceptance for the slice:** `dotnet test tests/Identity.Unit/Identity.Unit.csproj` is still green after your additions, the new tests run (not skipped), and the test count grows by the number of distinct scenarios you ported.

---

## 5. The work queue (Tier 1 — start here)

These are ordered by `coverage gain ÷ effort`. Don't pick later items until earlier ones are merged.

### T1.1 — `TokenRevocationServiceTests` (security-critical, no platform equivalent)

- **Source:** `tests/haworks.Tests.Unit/Services/TokenRevocationServiceTests.cs` (18 tests)
- **Target:** `tests/Identity.Unit/Services/TokenRevocationServiceTests.cs` (new file, new folder)
- **Platform code under test:** find via `grep -rn "TokenRevocation" src/Identity/`
- **Notes:** uses `IDistributedCache` (L2) + `IMemoryCache` (L1). Mock both; verify both are checked + populated. Per CLAUDE.md mandate, JTI revocation is non-negotiable for logout — these tests gate that guarantee.

### T1.2 — `RefreshTokenServiceTests`

- **Source:** `tests/haworks.Tests.Unit/Services/RefreshTokenServiceTests.cs` (15 tests)
- **Target:** `tests/Identity.Unit/Services/RefreshTokenServiceTests.cs` (new file)
- **Notes:** rotation counter must reject stale tokens. Map to platform's `IRefreshTokenService` (find via grep).

### T1.3 — `WebhookIdempotencyGuardTests`

- **Source:** `tests/haworks.Tests.Unit/Payments/WebhookIdempotencyGuardTests.cs` (12 tests)
- **Target:** `tests/Payments.Unit/WebhookIdempotencyGuardTests.cs`
- **Notes:** the platform's idempotency lives in `PaymentWebhookValidatedConsumer` + the `WebhookEvent` unique index. The platform may not have a separate "guard" class — if not, port the *behaviour* (idempotent on replay) as integration tests against the consumer + DB. Read `tests/Payments.Integration/WebhookFlowsTests.cs` first to see what's already covered.

### T1.4 — Validators sweep

For each of these, port to `tests/<Context>.Unit/Validators/<Name>Tests.cs`:

| Source file | Target file | Estimated tests |
|---|---|---|
| `tests/haworks.Tests.Unit/Validators/AuthValidatorTests.cs` | `tests/Identity.Unit/Validators/` (split per command) | ~25 |
| `tests/haworks.Tests.Unit/Validators/UserValidatorTests.cs` | `tests/Identity.Unit/Validators/` | ~44 |
| `tests/haworks.Tests.Unit/Validators/ProductValidatorTests.cs` | `tests/Catalog.Unit/Validators/` | ~39 |
| `tests/haworks.Tests.Unit/Validators/CategoryValidatorTests.cs` | `tests/Catalog.Unit/Validators/` | ~7 |
| `tests/haworks.Tests.Unit/Validators/CheckoutValidatorTests.cs` | the platform doesn't have a single CheckoutValidator — split between `Orders.Unit/Validators/` (cart-level rules) and `CheckoutOrchestrator` (saga input rules). Escalate if unsure. | ~30 |
| `tests/haworks.Tests.Unit/Validators/SubscriptionValidatorTests.cs` | the platform may not have subscriptions yet — **escalate before porting**. | ~9 |

The platform's per-command validator file convention is `tests/<Context>.Unit/Validators/<CommandName>ValidatorTests.cs` matching `src/<Context>/<Context>.Application/Validators/<CommandName>Validator.cs`. **One validator file per command, not per controller.**

### T1.5 — Outbox dedup integration

- **Source:** `tests/haworks.Tests.integration/Outbox/{OutboxWiringIntegrationTests,OutboxInboxAutomaticBehaviorProbe}.cs` (7 tests total)
- **Target:** **new project** `tests/BuildingBlocks.Testing.Integration/` OR add to an existing context's `.Integration` if scoped (e.g. payments outbox dedup goes to `Payments.Integration`).
- **Notes:** these test MassTransit's inbox dedup against the EF outbox. The platform has the outbox wired but no dedicated tests. Use the harness pattern; run against a Testcontainers postgres.

---

## 6. Tests that should NOT be ported (skip these)

- `tests/haworks.Tests.Unit/Commands/Checkout/InitiateCheckoutCommandHandlerTests.cs` — the god-handler this tests is replaced by the saga. Saga tests are the new source of truth (`tests/CheckoutOrchestrator.Integration/`).
- `tests/haworks.Tests.Unit/Payments/PayPal*Tests.cs` (anything with PayPal in the name) — platform is Stripe-only.
- `tests/haworks.Tests.Unit/Payments/WebhookRouterTests.cs` — platform doesn't have a router; each provider has its own consumer.
- `tests/haworks.Tests.Unit/Payments/PaymentGatewayTests.cs` — platform's per-provider service replaces this abstraction.
- Any monolith test that asserts on a join across two contexts (e.g. orders + catalog products) — by ADR-0004 those joins don't exist in the platform.

When in doubt, escalate (§8) rather than porting something that doesn't fit.

---

## 7. Per-task workflow

For each item you pick from §5:

1. **Branch:**
   ```
   git checkout -b port/test-<short-name>      # e.g. port/test-token-revocation
   ```
2. **Read first:**
   - The monolith source test file (top to bottom).
   - The corresponding platform code under test.
   - Any sibling test in the same target project for shape.
3. **Write tests** following §3 + §4.
4. **Build the target test project:**
   ```
   dotnet build tests/<Context>.Unit/<Context>.Unit.csproj
   ```
5. **Run only your suite:**
   ```
   dotnet test tests/<Context>.Unit/<Context>.Unit.csproj --filter "FullyQualifiedName~<YourClassName>"
   ```
6. **Run the whole context to confirm no regressions:**
   ```
   dotnet test tests/<Context>.Unit/<Context>.Unit.csproj
   ```
7. **For integration suites:** verify Docker is up first (`docker info`). Tests will fail fast if not.
8. **Acceptance criteria** (all must hold):
   - [ ] `dotnet build <target>.csproj` is clean (zero warnings under `TreatWarningsAsErrors`).
   - [ ] All new tests run (not `[Fact(Skip=...)]`) and pass.
   - [ ] No reference to any namespace from the monolith. Search for `Haworks.haworks` or non-platform paths in the diff and reject them.
   - [ ] Test count in the project grew by the number of distinct scenarios ported (after fold-parameterised-duplicates).
   - [ ] No `Task.Delay` added.
   - [ ] No `InMemoryDatabase` added (integration only).
9. **Commit per task** using this style:
   ```
   tests(<context>): port <name> from monolith — <count> scenarios

   <one paragraph: which monolith file, what slice, what's now covered.
   Note any folds or skips and why.>

   Source: tests/haworks.Tests.Unit/...
   Co-Authored-By: <your model name> <noreply@...>
   ```
10. **Open one PR per branch.** PR body must include:
    - Source file(s) ported
    - Target file(s) created
    - Test count delta (`dotnet test --list-tests` before/after)
    - Anything you skipped + why
    - Anything you escalated (§8)

Don't batch unrelated tasks into one PR. One Tier 1 item = one PR.

---

## 8. Escalation — when to stop and ask

Stop and write a short note in the PR (or surface to the human) when:

1. **The platform handler / service / endpoint doesn't exist.** Don't invent it. Note which monolith test demanded it; pick a different task.
2. **The monolith test relies on a feature that's been removed by ADR.** Drop the test, note the ADR you relied on (e.g. ADR-0003 for "saga is its own service" obsoletes god-handler tests).
3. **The platform handler has a different signature and you can't tell what the equivalent assertion should be.** Port a smaller, unambiguous test first; flag the ambiguous one.
4. **The test requires infrastructure that isn't wired** (e.g. Content's `IFileValidator`/`IChunkedUploadService` are missing per `tests/Content.Integration/Controllers/ContentControllerTests.cs` skip reasons). Add a `[Fact(Skip = "Pending: <interface> implementation")]` and move on.
5. **Tests start passing locally but fail in CI** with a docker/testcontainers error. Check that your test project links `TestModuleInitializer.cs`. If still failing, escalate.

---

## 9. Long-term backlog (not yet prioritised — do not start until Tier 1 is done)

Tier 2 (high-value integration):
- Consumer integration tests consolidation (`PaymentCompletedConsumer`, `PaymentSessionConsumer`, `StockReservation/Release`)
- Vault rotation tests (`VaultDynamicCredentialsIntegrationTests`, `VaultRotationUnderLoadTests`)

Tier 3 (domain breadth):
- `OrderTests`, `PaymentTests`, `ProductTests`, `UserTests` domain entity tests
- Category CRUD tests (`Commands/Categories/*`)
- Review moderation tests (`Commands/Reviews/*`)

Tier 4 (infra reliability):
- `UserEmailServiceTests`
- DB migration regression tests

Tier 5 (whole-suite gaps — net-new projects):
- `tests/Smoke/` — port `tests/haworks.Tests.Smoke/` shape
- `tests/E2E/` — port `tests/haworks.Tests.E2E/CheckoutE2ETests.cs` (Playwright + WireMock)

The full gap report is at `docs/agent-briefs/test-port-gap-report.md` (sibling file). Read it before picking from this backlog.

---

## 10. Reference: useful greps

```bash
# Find platform code under test by name
grep -rn "class TokenRevocation" /Users/chidionyema/Documents/code/haworks-platform/src/

# Find existing platform tests covering a topic before duplicating
grep -rln "JwtTokenService" /Users/chidionyema/Documents/code/haworks-platform/tests/

# Find the canonical platform error name
grep -rn "Error.Identity\." /Users/chidionyema/Documents/code/haworks-platform/src/

# List tests in a project (count delta check)
dotnet test tests/Identity.Unit/Identity.Unit.csproj --list-tests --no-build | wc -l

# Confirm no monolith refs leaked in a diff
git diff --name-only main | xargs grep -l "haworks\.\(Tests\|Domain\|Application\|Infrastructure\)" 2>/dev/null
```

---

## 11. One-time setup verification

Before opening your first PR, run this to confirm your environment is healthy:

```bash
cd /Users/chidionyema/Documents/code/haworks-platform
dotnet build HaworksPlatform.sln
docker info >/dev/null && echo "docker ok" || echo "START DOCKER"
dotnet test tests/Identity.Unit/Identity.Unit.csproj --no-build
```

All three must succeed. If `dotnet test` fails on `main`, that's a pre-existing bug — flag it, don't try to fix it inside a porting PR.

---

## 12. What "done with the whole project" looks like

When all of §5 + §9 are merged:
- Unit test count in `haworks-platform/tests/` reaches ≥80% of the monolith's count after deduplication and feature-removal exclusions.
- A `Smoke` and `E2E` project exist and are wired into CI (see `.github/workflows/ci.yml`).
- README's `## Test Inventory` table reflects the new totals.
- The case study (`docs/CASE-STUDY.md`) section "Test pyramid" is updated.

---

**End of brief.** Pick T1.1 to start.
