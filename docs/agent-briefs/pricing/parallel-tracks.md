```
REPO=/Users/chidionyema/Documents/code/ritualworks-platform
GH_REPO=chidionyema/ritualworks-platform
BASE_BRANCH=feat/pricing-service
BRIEF_FILE=docs/agent-briefs/pricing/parallel-tracks.md
TRACK_PREFIX=feat/pricing-
TRACKS=(T1 T2 T3 T4)
WORKTREE_PARENT=/Users/chidionyema/.gemini/tmp/ritualworks-platform
```

# Pricing service — parallel tracks (Mode B brief)

After L0 lands on `feat/pricing-service`, the four L1 phases (domain, logic, api, events) can run **in parallel** on separate branches. This file is the Mode B brief that drives that wave.

## Universal rules

### File-scope discipline (THE contract)
Each track owns a disjoint set of files. **You do not touch files outside your track's "Files you own" list.** Period.
The shared orchestrator `src/Pricing/Pricing.Application/DependencyInjection.cs` is written ONCE by L0 and MUST NOT be modified by any track. Each track fills its own `DependencyInjection.<Track>.cs` sibling.

### Build verify per file group
Before every commit, build the projects you touched:
```bash
dotnet build "$WT/src/Pricing/Pricing.<Project>" --nologo --verbosity quiet
```
Must exit 0.

### Push cadence
Per file group: commit + push immediately. Not "one big commit at end."

### Done check
Every track ends with a `Done:` shell command. Run it verbatim. Must exit 0 before the auto-merge PR opens.

## Anti-stuck
- **60-second decision time-box.** Naming, file location, dep choice over budget? Mirror the reference file and move on. Don't deliberate.
- **If thinking instead of doing, you are stuck.** Mirror the reference. Move on.
- **Cross-track need? `// TODO(pricing-<TRACK>): <reason>` and continue.** Don't patch sibling code.
- **No questions to user.** Operator is not in session.

## Reference file
When in doubt, mirror `src/Catalog/` (for persistence/API) or `src/Notifications/` (for consumers).

### Track T1: Domain Models & Persistence
**Files you own (exclusive):**
- `src/Pricing/Pricing.Domain/Aggregates/**`
- `src/Pricing/Pricing.Domain/ValueObjects/**`
- `src/Pricing/Pricing.Domain/Enums/**`
- `src/Pricing/Pricing.Infrastructure/Persistence/Configurations/**`
- `src/Pricing/Pricing.Infrastructure/Persistence/Migrations/**`
- `src/Pricing/Pricing.Application/DependencyInjection.Domain.cs`

**Files you may NOT touch:**
- `src/Pricing/Pricing.Application/DependencyInjection.cs`
- `src/Pricing/Pricing.Infrastructure/Persistence/PricingDbContext.cs`

**Reference to mirror:** `src/Catalog/Catalog.Domain/Product.cs`
**NuGet (if any):** none

### Work plan
1. **Entities** — Implement `Promotion`, `PromotionRule`, and `PromotionRedemption` as domain entities per spec § 4.
2. **Persistence** — Create EF configurations in `Pricing.Infrastructure/Persistence/Configurations/`.
3. **Migrations** — Generate the migration for the new tables.
4. **DI** — Wire the domain-related services in `DependencyInjection.Domain.cs`.

**Done:** `dotnet build "$WT/src/Pricing/Pricing.Domain/Pricing.Domain.csproj" && dotnet build "$WT/src/Pricing/Pricing.Infrastructure/Pricing.Infrastructure.csproj"`

### Track T2: Business Logic (Resolver & Calculator)
**Files you own (exclusive):**
- `src/Pricing/Pricing.Application/Promotions/**`
- `src/Pricing/Pricing.Application/DependencyInjection.Logic.cs`
- `tests/Pricing.Unit/Promotions/**`

**Files you may NOT touch:**
- `src/Pricing/Pricing.Application/Queries/**`
- `src/Pricing/Pricing.Application/Commands/**`
- `src/Pricing/Pricing.Application/DependencyInjection.cs`

**Reference to mirror:** `src/Audit/Audit.Application/Extraction/ReflectionAuditExtractor.cs`
**NuGet (if any):** none

### Work plan
1. **Resolver** — Implement `IPromotionResolver` to filter and sort applicable promotions per spec § 5.1.
2. **Matchers** — Implement rule matchers for each `rule_type` defined in spec § 5.2.
3. **Calculator** — Implement `IDiscountCalculator` for `PERCENT_OFF` and `FIXED_AMOUNT` per spec § 5.3.
4. **Unit Tests** — Add comprehensive tests in `tests/Pricing.Unit/Promotions/` covering edge cases.

**Done:** `dotnet test "$WT/tests/Pricing.Unit/Pricing.Unit.csproj" -c Release --nologo --filter "FullyQualifiedName~Promotions"`

### Track T3: API & Commands/Queries
**Files you own (exclusive):**
- `src/Pricing/Pricing.Api/Controllers/**`
- `src/Pricing/Pricing.Application/Queries/**`
- `src/Pricing/Pricing.Application/Commands/**`
- `src/Pricing/Pricing.Application/DependencyInjection.Api.cs`
- `tests/Pricing.Integration/Api/**`

**Files you may NOT touch:**
- `src/Pricing/Pricing.Application/Promotions/**`
- `src/Pricing/Pricing.Application/DependencyInjection.cs`

**Reference to mirror:** `src/Catalog/Catalog.Api/Controllers/ProductsController.cs`
**NuGet (if any):** none

### Work plan
1. **Quote Endpoint** — Implement `POST /price/quote` handler and controller per spec § 3.1.
2. **Admin CRUD** — Implement `POST /promotions`, `GET /promotions` (paginated), and `DELETE /promotions/{id}`.
3. **Validation** — Add MediatR behaviors and validators for all commands/queries.
4. **Integration Tests** — Implement end-to-end API tests in `tests/Pricing.Integration/Api/`.

**Done:** `dotnet test "$WT/tests/Pricing.Integration/Pricing.Integration.csproj" -c Release --nologo --filter "FullyQualifiedName~Api"`

### Track T4: Events & Cache Integration
**Files you own (exclusive):**
- `src/Pricing/Pricing.Application/Consumers/**`
- `src/Pricing/Pricing.Infrastructure/Cache/**`
- `src/Pricing/Pricing.Application/DependencyInjection.Events.cs`
- `tests/Pricing.Integration/Consumers/**`

**Files you may NOT touch:**
- `src/Pricing/Pricing.Api/Controllers/**`
- `src/Pricing/Pricing.Application/DependencyInjection.cs`

**Reference to mirror:** `src/Notifications/Notifications.Application/Consumers/NotificationDispatchConsumer.cs`
**NuGet (if any):** none

### Work plan
1. **Redemption Consumer** — Implement `OrderCreated` consumer to record redemptions per spec § 3.2.
2. **Cache Invalidation** — Implement `ProductCacheInvalidated` consumer to purge Redis cache.
3. **Cache Implementation** — Implement the Redis-backed quote caching logic per spec § 5.1.
4. **Integration Tests** — Verify consumer behavior and cache invalidation in `tests/Pricing.Integration/Consumers/`.

**Done:** `dotnet test "$WT/tests/Pricing.Integration/Pricing.Integration.csproj" -c Release --nologo --filter "FullyQualifiedName~Consumers"`
