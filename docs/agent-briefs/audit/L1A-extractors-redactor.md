# Brief L1.A — Extractors + redactor

## Goal
Implement the extractor and redactor surfaces that turn an inbound `IDomainEvent` into a redacted `AuditRow` ready for insert. Pure unit-testable code. No persistence yet.

## Phase / blocks-on
Phase L1.A. Blocks-on: L0 committed.

## Inputs
1. `docs/agent-briefs/audit/README.md`.
2. `docs/agent-briefs/audit-service-spec.md` — sections 5.1 (extraction) and 5.2 (redaction). The spec is authoritative on the deny-list and the hand-written extractor overrides.
3. `src/Audit/Audit.Domain/AuditEvent.cs` — to confirm the `AuditRow` value object's shape (or create it as a record in `Audit.Application/Extraction/AuditRow.cs` if L0 didn't put it in Domain — see hard stops).
4. `src/Contracts/` — full directory tree. Inspect every event the extractor must handle. Pay attention to the three the spec calls out:
   - `Catalog/StockReservationFailedEvent.cs`
   - `Identity/VaultRotationStageEvent.cs`
   - `Catalog/ProductCacheInvalidatedEvent.cs`
5. `src/Contracts/IDomainEvent.cs` — confirm the interface.

## Deliverable

### New files
- `src/Audit/Audit.Application/Extraction/AuditRow.cs` — `record AuditRow(...)` per spec § 5.1. Use `JsonElement` for payload + metadata.
- `src/Audit/Audit.Application/Extraction/IAuditExtractor.cs` — the generic interface (already created in L0 as a stub; replace with the real signature).
- `src/Audit/Audit.Application/Extraction/ReflectionAuditExtractor.cs` — default implementation. Property-name lookup order per spec: `OrderId, UserId, PaymentId, SkuId, ProductId, CartId`. First match wins; entity_type derived from property name (lowercased, `-Id` stripped).
- `src/Audit/Audit.Application/Extraction/Overrides/StockReservationFailedExtractor.cs` — picks `OrderId`.
- `src/Audit/Audit.Application/Extraction/Overrides/VaultRotationStageExtractor.cs` — `entity_type="system", entity_id=ServiceName`.
- `src/Audit/Audit.Application/Extraction/Overrides/ProductCacheInvalidatedExtractor.cs` — `entity_type="cache", entity_id=CacheKey`.
- `src/Audit/Audit.Application/Extraction/ExtractorRegistry.cs` — DI helper that registers `ReflectionAuditExtractor<T>` for every `IDomainEvent` plus the three overrides. Closed-generic registration.
- `src/Audit/Audit.Application/Redaction/ISecretRedactor.cs` — already a stub from L0; replace with real signature: `JsonElement Redact(JsonElement input)`.
- `src/Audit/Audit.Application/Redaction/SecretRedactor.cs` — implementation per spec § 5.2: case-insensitive property suffix match (`token|password|secret|key|credential|apikey|authorization`), drop. Strip `RawBody`, replace with `RawBodySha256`. Credit-card regex with Luhn validation → `****<last4>`. CVV fields drop.
- `tests/Audit.Unit/Audit.Unit.csproj` — new test project (xUnit + FluentAssertions, match `tests/Notifications.Unit/Notifications.Unit.csproj`).
- `tests/Audit.Unit/Extraction/ReflectionAuditExtractorTests.cs` — golden inputs per representative event (one Orders, one Payments, one Catalog, plus the three with overrides).
- `tests/Audit.Unit/Extraction/OverrideTests.cs` — one test per hand-written extractor.
- `tests/Audit.Unit/Redaction/SecretRedactorTests.cs` — every redaction rule, plus a fuzzer (200 random JSON docs, assert no property matching the deny-list survives).

### Modified files
- `src/Audit/Audit.Application/DependencyInjection.cs` — extension `AddAuditExtractors(this IServiceCollection)` calls `ExtractorRegistry.Register(services)` and registers `ISecretRedactor` as a singleton. (Create this DI file if L0 didn't add one.)
- `RitualworksPlatform.sln` — add `tests/Audit.Unit/Audit.Unit.csproj`.

## Acceptance

```bash
cd /Users/chidionyema/Documents/code/rw-audit

dotnet build src/Audit/Audit.Application/Audit.Application.csproj -c Release --nologo --verbosity quiet
dotnet test  tests/Audit.Unit/Audit.Unit.csproj                   -c Release --nologo --logger "console;verbosity=minimal"
```

Test count: at least 12 (3 reflection goldens + 3 override goldens + 6 redactor cases). 0 failures.

Commit:
```bash
git add -A
git commit -m "feat(audit/L1.A): extractors + secret redactor + unit tests

ReflectionAuditExtractor handles every event whose entity-id property is
named conventionally; per-event extractors override where the choice is
ambiguous (StockReservationFailedEvent picks OrderId).

SecretRedactor strips token/password/secret/key/credential properties
plus credit-card numbers (Luhn-validated) + CVVs. Deny-list now;
allow-list when contracts settle.

Per docs/agent-briefs/audit-service-spec.md § 5.1, § 5.2."
```

## Hard stops — parallel-scope

L1.A runs in PARALLEL with L1.B / L1.C / L1.D. You touch ONLY these paths:

- `src/Audit/Audit.Application/Extraction/**` (write whatever you need here)
- `src/Audit/Audit.Application/Redaction/**`
- `src/Audit/Audit.Application/DependencyInjection.Extractors.cs` (fill in the body — do NOT modify the surrounding `DependencyInjection.cs` or any sibling `DependencyInjection.*.cs`)
- `tests/Audit.Unit/Extraction/**`, `tests/Audit.Unit/Redaction/**` (only your test files; do NOT modify `tests/Audit.Unit/Audit.Unit.csproj` — L0 set it up)

If you need a change anywhere else, file a blocker.

Plus the standard hard stops:

- Do NOT touch L0's project structure (csproj refs, DbContext, Aspire wiring).
- Do NOT add MassTransit consumer logic — that's L1.B.
- Do NOT add controllers — that's L1.C.
- Do NOT add EF migrations — that's L1.B (events) or L1.D (export jobs).
- Do NOT introduce a JSON library other than `System.Text.Json` (already in BCL).
- The redactor's deny-list is exactly what the spec lists. Don't add extra patterns "just in case".
- Use `JsonElement` not `JsonNode` for performance — payloads stay immutable through the redactor.

## Done-report format

Per `README.md`.
