# Audit — decoupling refactor (modify existing service)

> **Status: Complete.** Audit service is now event-shape-agnostic. All typed extractors replaced with reflection-based extraction.

**Mode:** modify-existing-service. Run with `wave run`; auto-detect should set `WAVE_MODE=modify`.

**Goal:** drop Audit's compile-time knowledge of specific Catalog and Identity event types. After this lands, `scripts/check-architecture.sh` should report 0 hard violations AND 0 soft warnings for Audit. The `using Haworks.Contracts.Catalog;` and `using Haworks.Contracts.Identity;` lines should not exist anywhere under `src/Audit/`.

**Why this matters:** see `docs/architecture/cross-cutting-coupling-audit.md` § Audit. Audit is the most-leverage cross-cutting service in the platform; making it event-shape-agnostic unlocks drop-in reuse in other projects.

## Current state

`src/Audit/Audit.Application/Extraction/` contains:
- `IAuditExtractor<T>` — abstract per-event extractor interface
- `BaseAuditExtractor.cs` — shared base
- `ReflectionAuditExtractor.cs` — generic extractor that walks any `IDomainEvent` via reflection
- **Coupled:** `ProductCacheInvalidatedExtractor.cs`, `StockReservationFailedExtractor.cs`, `VaultRotationStageExtractor.cs` — typed against specific Contracts.Catalog/Contracts.Identity events
- `ExtractorRegistry.cs` — registers the typed extractors as overrides for their specific event types

The typed extractors exist because reflection alone doesn't always produce the right `entity_type` / `entity_id` tuple. For example, `StockReservationFailedEvent.OrderId` should map to `entity_type="order"`, but reflection sees it as a property called `OrderId` on a stock-reservation event and might pick `SagaId` instead.

## Target state

Audit knows nothing about Catalog or Identity at compile time. Per-event overrides become **runtime config** stored in a new `audit_extraction_overrides` table, keyed by event-type FQN string. Operators add overrides without recompiling Audit.

```
audit_extraction_overrides
├── id                  uuid   PK
├── event_type          text   UNIQUE  (e.g. "Haworks.Contracts.Catalog.StockReservationFailedEvent")
├── entity_type         text             (override the default)
├── entity_id_path      text             (jsonpath into the event payload)
├── redact_paths        jsonb            (paths to redact from payload)
└── version             int              (optimistic concurrency)
```

`AuditConsumer<T>` workflow:
1. Receive any `IDomainEvent` (no compile-time knowledge of T's specific shape).
2. Lookup `audit_extraction_overrides` by `typeof(T).FullName` (cached in memory, refreshed on a schedule).
3. If override exists: use it to compute `entity_type` + `entity_id` + redaction list.
4. If no override: fall through to `ReflectionAuditExtractor<T>` (default heuristics — first `*Id` property, etc.).
5. Write the audit row.

## Track decomposition (4 parallel tracks after L0)

L0 doesn't apply — modify-mode. The brief commits straight to `feat/audit-decoupling`; agents fork from there.

### Track T1: migration + override entity
- New EF migration: `audit_extraction_overrides` table per § Target.
- `AuditExtractionOverride` entity in `Audit.Domain/`.
- Read-side: `IAuditExtractionOverrideRepository` + impl that loads all overrides into a `Dictionary<string, AuditExtractionOverride>` cached for 60s.
- Done: `dotnet test tests/Audit.Integration --filter "FullyQualifiedName~AuditExtractionOverride"` passes.

### Track T2: refactor `AuditConsumer<T>` to consult overrides
- Modify `Audit.Application/Capture/AuditConsumer.cs` (or wherever the consumer lives) to:
  - Resolve `IAuditExtractionOverrideRepository` from DI
  - Lookup by `typeof(T).FullName`
  - Apply override or fall through to `ReflectionAuditExtractor<T>`
- Update unit tests for the new consumer behavior.
- Done: `dotnet test tests/Audit.Unit --filter "FullyQualifiedName~AuditConsumer"` passes.

### Track T3: delete typed extractors
- Delete `ProductCacheInvalidatedExtractor.cs`, `StockReservationFailedExtractor.cs`, `VaultRotationStageExtractor.cs` and their tests.
- Update `ExtractorRegistry.cs` — should no longer register them (and probably can be deleted / inlined).
- For each deleted extractor, add the equivalent INSERT into `audit_extraction_overrides` as a seed migration so existing audit behavior is preserved.
- Done: `grep -rE "using Haworks\\.Contracts\\.(Catalog|Identity);" src/Audit/` returns empty.

### Track T4: integration test — overrides honored
- New test: publish a `StockReservationFailedEvent` (using `Haworks.Contracts.Catalog.StockReservationFailedEvent` from the test assembly — that's allowed, tests can know about it). Assert the audit row has `entity_type="order"` and `entity_id` matches the event's `OrderId`. With override seeded by T3.
- Same for `VaultRotationStageEvent`.
- Done: `dotnet test tests/Audit.Integration --filter "FullyQualifiedName~OverrideHonored"` passes; `bash scripts/check-architecture.sh` shows Audit clean.

## Reference files
- `src/Audit/Audit.Application/Extraction/ReflectionAuditExtractor.cs` (the existing reflection-based path — model your override consumption after this)
- `src/Audit/Audit.Application/Extraction/StockReservationFailedExtractor.cs` (the typed override that's being deleted — read it to understand what its override needs to express)
- `src/Audit/Audit.Infrastructure/Migrations/` (existing migration shape)

## Done check (whole feature)
```
dotnet test tests/Audit.Integration tests/Audit.Unit -c Release --nologo
bash scripts/check-architecture.sh
# both must exit 0; check-architecture must report 0 warnings for Audit
```
