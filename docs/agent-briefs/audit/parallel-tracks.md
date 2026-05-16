# Audit service — parallel L1 tracks (Mode B brief)

After L0 lands on `feat/audit-service`, the four L1 phases (extractors+redactor, capture pipeline, query API, export+partition cron) can run **in parallel** on separate branches. This file is the Mode B brief that drives that wave: one file, four mutually-independent `### Track Lx` sections, and shared universal-rules / anti-stuck / reference-file headers.

Use this with `docs/agent-briefs/SUPERPROMPT.md` Mode B. Set:

```
REPO=/Users/chidionyema/Documents/code/haworks-platform
GH_REPO=chidionyema/haworks-platform
BASE_BRANCH=feat/audit-service                          # NOT main — we merge into the audit integration branch
BRIEF_FILE=docs/agent-briefs/audit/parallel-tracks.md
TRACK_PREFIX=feat/audit-
TRACKS=(L1A L1B L1C L1D)
WORKTREE_PARENT=/tmp
```

L0 must be merged into `feat/audit-service` before launching these. The four L1 PRs target `feat/audit-service`, not `main`. When all four merge, open a single rollup PR `feat/audit-service` → `main`.

The authoritative design is `docs/agent-briefs/audit-service-spec.md`. Each track section here points to spec sections.

---

## Universal rules

### File-scope discipline (THE contract)

Each track owns a disjoint set of files. **You do not touch files outside your track's "Files you own" list.** Period. The four L1 phases merge cleanly back to `feat/audit-service` because the file scopes don't overlap; if your run touches a file outside your scope, the merge will conflict and your PR will be rejected.

The only file shared between phases is `src/Audit/Audit.Application/DependencyInjection.cs` — it was written ONCE by L0 and orchestrates the four phase-specific extensions. **No L1 phase modifies it.** Each phase fills in its own `DependencyInjection.<Phase>.cs` sibling.

### Build verify per file group

Before every commit, build only the projects your group touched:

```bash
dotnet build "$WT/src/Audit/Audit.<Project>" --nologo --verbosity quiet
```

Must exit 0. If it fails, fix forward; don't commit broken builds.

### Push cadence

Per file group: commit + push immediately. Not "one big commit at end." Order matters:

```bash
git -C "$WT" add <explicit list of files in this group>
git -C "$WT" commit -m "<type>(audit-<TRACK>): <one-line summary>"
git -C "$WT" push origin "feat/audit-${TRACK_LOWERCASE}"
```

### Done check

Every track ends with a `Done:` shell command. Run it verbatim. Must exit 0 before the auto-merge PR opens.

### Test conventions

- Unit tests live under `tests/Audit.Unit/<your-area>/` (created in L0 as a skeleton — you only add files in your subdirectory; do not modify the .csproj).
- Integration tests live under `tests/Audit.Integration/` (also created in L0). You may add new test files; you may NOT modify the existing `AuditWebAppFactory.cs` except as your track explicitly says (L1.B is the only phase allowed to refine it).

### No new packages outside what your track names

Your track section names at most ONE NuGet package you may add. Don't add others. Don't restructure ItemGroups.

---

## Anti-stuck

- **60-second decision time-box.** Naming, file location, dep choice over budget? Mirror the reference file (below) and move on. Don't deliberate.
- **If thinking instead of doing, you are stuck.** Mirror the reference. Move on. Re-reading the brief for the third time is a symptom.
- **Cross-track need? `// TODO(audit-<TRACK>): <reason>` and continue.** Don't patch sibling code; the comment is enough.
- **Spec ambiguous?** Mirror the closest existing analog in `src/Notifications/`. Still ambiguous? Pick the simpler option, add a `// TODO(audit-<TRACK>)`, proceed.
- **No questions to user.** Operator is not in session. Decide locally.

---

## Reference file

When in doubt about file structure, naming, csproj shape, DI patterns, or test layout, **mirror `src/Notifications/`**. It's the most-recently-shipped service in this repo and the patterns there are the platform-canonical ones.

Specific mirrors per track:
- L1.A → mirror `src/Notifications/Notifications.Application/Suppression/` (single-purpose Application subfolder with interfaces + impl + DI extension).
- L1.B → mirror `src/Notifications/Notifications.Application/Consumers/NotificationDispatchConsumer.cs` for the consumer shape, `tests/Notifications.Integration/PipelineTests.cs` for the integration test shape.
- L1.C → mirror `src/Notifications/Notifications.Api/Controllers/NotificationsController.cs` for the controller + role gate, `src/Notifications/Notifications.Application/Commands/SendNotificationCommand.cs` for the MediatR shape.
- L1.D → mirror `src/Content/Content.Infrastructure/` for the Storage abstraction usage, `src/Notifications/Notifications.Infrastructure/Outbox/` (or whatever the platform's existing BackgroundService example is) for the BackgroundService shape.

---

### Track L1A: Extractors + redactor

**Files you own (exclusive):**
- `src/Audit/Audit.Application/Extraction/**`
- `src/Audit/Audit.Application/Redaction/**`
- `src/Audit/Audit.Application/DependencyInjection.Extractors.cs` (replace L0's stub body)
- `tests/Audit.Unit/Extraction/**`, `tests/Audit.Unit/Redaction/**`

**Files you may NOT touch:**
- `src/Audit/Audit.Application/DependencyInjection.cs` (L0's orchestrator)
- Any other `DependencyInjection.<Phase>.cs` sibling
- Any project's `.csproj` — packages your track needs are already in L0's csproj
- `src/Audit/Audit.Domain/AuditEvent.cs` (L0 owns the entity)
- `src/Audit/Audit.Application/Extraction/AuditRow.cs` if L0 already created it (just add adjacent files)

**Reference to mirror:** `src/Notifications/Notifications.Application/Suppression/` — single-purpose subfolder with interfaces + impl + DI extension.

**NuGet (if any):** none.

**Spec sections:** § 5.1 (extraction), § 5.2 (redaction).

### Work plan

1. **Extractor surface** — `IAuditExtractor<T>` is already declared in L0; add `ReflectionAuditExtractor<T>` (default, looks for `OrderId`/`UserId`/`PaymentId`/`SkuId`/`ProductId`/`CartId` in that order) + three hand-written overrides (`StockReservationFailedExtractor` picks `OrderId`, `VaultRotationStageExtractor` → `entity_type="system"`, `ProductCacheInvalidatedExtractor` → `entity_type="cache"`). Build verify, commit, push.

2. **Extractor registry** — `ExtractorRegistry.cs` reflects over `Haworks.Contracts` and registers `ReflectionAuditExtractor<T>` for every `IDomainEvent`, plus the three overrides. Wire from `DependencyInjection.Extractors.cs`. Build, commit, push.

3. **Redactor** — `SecretRedactor : ISecretRedactor` per spec § 5.2: case-insensitive property suffix match (`token|password|secret|key|credential|apikey|authorization`) drop; strip `RawBody` → replace with `RawBodySha256`; credit-card regex with Luhn validation → `****<last4>`; CVV fields drop. `JsonElement` in/out. Build, commit, push.

4. **Unit tests** — `ReflectionAuditExtractorTests` (3 representative events), `OverrideTests` (one per hand-written extractor), `SecretRedactorTests` (every rule + 200-doc fuzzer asserting nothing matching the deny-list survives). Build the test project, run the tests, commit, push.

**Done:** `dotnet test "$WT/tests/Audit.Unit/Audit.Unit.csproj" -c Release --nologo --logger "console;verbosity=minimal"` — exit 0, ≥12 tests passed, 0 failed.

---

### Track L1B: Capture pipeline

**Files you own (exclusive):**
- `src/Audit/Audit.Application/Capture/**`
- `src/Audit/Audit.Infrastructure/Persistence/AuditWriter.cs` (only this file — DbContext is L0's)
- `src/Audit/Audit.Infrastructure/Migrations/<your-timestamp>_AddAuditEventsPartitioned.*`
- `src/Audit/Audit.Application/DependencyInjection.Capture.cs`
- `tests/Audit.Integration/EndToEndCaptureTests.cs`, `tests/Audit.Integration/IdempotencyTests.cs`
- `tests/Audit.Integration/AuditWebAppFactory.cs` — you may REFINE the L0 skeleton (e.g., add Testcontainers RabbitMQ); you may NOT remove anything other phases depend on.

**Files you may NOT touch:**
- `src/Audit/Audit.Infrastructure/Persistence/AuditDbContext.cs`
- Any other phase's DI file
- Any project's `.csproj`

**Reference to mirror:** `src/Notifications/Notifications.Application/Consumers/NotificationDispatchConsumer.cs` (consumer shape), `tests/Notifications.Integration/PipelineTests.cs` (integration shape).

**NuGet (if any):** none — Npgsql + EF Core are already in Infrastructure.csproj.

**Spec sections:** § 4 (data model), § 5.3 (idempotency), § 5.4 (throughput).

### Work plan

1. **EF migration** — `Migrations/<ts>_AddAuditEventsPartitioned.cs` runs the spec § 4 SQL **verbatim** via `migrationBuilder.Sql(...)`: partitioned table, 3 indexes, first 2 monthly partitions, partial unique index on `(metadata->>'message_id')` per partition. Do NOT use EF fluent API for partitions. Commit, push.

2. **AuditWriter** — `AuditWriter : IAuditWriter` (interface from L0). COPY-batched via `System.Threading.Channels` (50 rows / 200ms threshold), `IDisposable.Flush()` on shutdown, uses `NpgsqlConnection.BeginBinaryImport` for COPY. Commit, push.

3. **Generic AuditConsumer<T>** — `class AuditConsumer<T> : IConsumer<T> where T : class, IDomainEvent`. Resolves `IAuditExtractor<T>` (from L1.A — interface stable in L0), calls `ISecretRedactor`, calls `IAuditWriter.WriteAsync`. Idempotency: read `ConsumeContext.MessageId`; on null, deterministic hash per spec. Commit, push.

4. **Consumer registry** — `AuditConsumerRegistry : IAuditConsumerRegistry` (interface from L0). Reflects over `Haworks.Contracts`, registers `AuditConsumer<TEvent>` for every `IDomainEvent` via the MassTransit configurator. Wire `DependencyInjection.Capture.cs` to register the writer + consumer registry. Commit, push.

5. **Integration tests** — `EndToEndCaptureTests` (4 representative events: `OrderCreatedEvent`, `PaymentCompletedEvent` with a `Token` to assert redaction, `StockReservationFailedEvent` to assert override picks `OrderId`, `VaultRotationStageEvent` to assert `entity_type="system"`); `IdempotencyTests` (same `MessageId` twice → one row). Use `SharedTestPostgres`. Build, run, commit, push.

**Done:** `dotnet test "$WT/tests/Audit.Integration/Audit.Integration.csproj" -c Release --nologo --filter "FullyQualifiedName~EndToEndCapture|FullyQualifiedName~Idempotency"` — exit 0, all assertions pass.

---

### Track L1C: Query API

**Files you own (exclusive):**
- `src/Audit/Audit.Application/Queries/**`
- `src/Audit/Audit.Api/Controllers/AuditController.cs`
- `src/Audit/Audit.Application/DependencyInjection.Queries.cs`
- `tests/Audit.Integration/QueryApiTests.cs`

**Files you may NOT touch:**
- `src/Audit/Audit.Api/Controllers/AuditExportController.cs` (L1.D's territory — it's a SEPARATE controller)
- `src/Audit/Audit.Infrastructure/Persistence/AuditDbContext.cs` (L0's)
- Any other phase's DI file
- Any project's `.csproj`

**Reference to mirror:** `src/Notifications/Notifications.Api/Controllers/NotificationsController.cs` (controller + role gate); `src/Notifications/Notifications.Application/Commands/SendNotificationCommand.cs` (MediatR shape).

**NuGet (if any):** none — FluentValidation is already in L0's Application.csproj.

**Spec sections:** § 3.1 (HTTP shapes), § 7 (SLA targets).

### Work plan

1. **Query DTOs + handler** — `GetAuditEventsQuery` record (filters: entityId, entityType, eventType, from, to, limit, cursor); `GetAuditEventsQueryHandler` (MediatR; EF query with filters; `.Take(limit + 1)` to determine `nextCursor`). `GetAuditEventByIdQuery` + handler. `AuditCursor` helper (base64 of `(occurred_at_ticks, id_guid)` tuple). Build, commit, push.

2. **Validator** — `GetAuditEventsQueryValidator : AbstractValidator<GetAuditEventsQuery>` per FluentValidation. Rules: `from < to`, `to - from <= 90 days`, `limit ∈ [1, 1000]`. Wire in `DependencyInjection.Queries.cs`. Build, commit, push.

3. **Controller** — `AuditController : ControllerBase`, `[Authorize(Roles="audit-reader")]`, `GET /audit/events`, `GET /audit/events/{id:guid}`. Returns the response shape from spec § 3.1. Build, commit, push.

4. **Integration tests** — `QueryApiTests`: happy-path with cursor follow-through (seed 25 rows, paginate with limit=10), filter-by-eventType, filter-by-entityId, 90-day cap rejection (400), 401 without JWT, 403 with wrong role, 404 for unknown id. Build, run, commit, push.

**Done:** `dotnet test "$WT/tests/Audit.Integration/Audit.Integration.csproj" -c Release --nologo --filter "FullyQualifiedName~QueryApi"` — exit 0, all 6+ scenarios pass.

---

### Track L1D: Export + partition cron

**Files you own (exclusive):**
- `src/Audit/Audit.Application/Export/**`
- `src/Audit/Audit.Infrastructure/Export/**`
- `src/Audit/Audit.Infrastructure/Partitions/**`
- `src/Audit/Audit.Infrastructure/Migrations/<your-timestamp>_AddAuditExportJobs.*` (separate timestamp from L1.B's events migration)
- `src/Audit/Audit.Api/Controllers/AuditExportController.cs`
- `src/Audit/Audit.Application/DependencyInjection.Export.cs`
- `tests/Audit.Integration/ExportJobTests.cs`, `tests/Audit.Integration/PartitionRolloverTests.cs`

**Files you may NOT touch:**
- `src/Audit/Audit.Api/Controllers/AuditController.cs` (L1.C)
- `src/Audit/Audit.Infrastructure/Persistence/AuditDbContext.cs` (L0 — the `AuditExportJobs` DbSet is already declared)
- L1.B's migration file
- Any other phase's DI file
- Any project's `.csproj`

**Reference to mirror:** `src/Content/Content.Infrastructure/` for the Storage abstraction usage; the platform's existing `BackgroundService` examples for the cron shape.

**NuGet (if any):** at most one — if CSV writing isn't already available via existing references, you may add `CsvHelper`. Verify first; don't add what's already there.

**Spec sections:** § 3.1 (export endpoints), § 4 (partition retention rules).

### Work plan

1. **Export job entity + migration** — `AuditExportJob` entity already mapped to `audit_export_jobs` DbSet in L0's DbContext; you add the migration that creates the table (`<ts>_AddAuditExportJobs`). Columns: `id, status, requested_by, request_json, started_at, completed_at, download_url, error`. Build, commit, push.

2. **Export request + status DTOs + interface** — `AuditExportRequest` (same filter shape as `GetAuditEventsQuery` minus cursor/limit), `AuditExportStatus` enum, `IAuditExportJob` interface (already declared in L0; you implement). Build, commit, push.

3. **Export worker** — `AuditExportWorker : BackgroundService` consumes a `Channel<AuditExportWorkItem>`, streams rows via `IAsyncEnumerable`, writes CSV via `CsvHelper` (or hand-rolled), uploads to S3 via the existing `Storage` abstraction in BuildingBlocks, sets `audit_export_jobs.status` to `succeeded` with the signed URL. Build, commit, push.

4. **Export controller** — `AuditExportController : ControllerBase`. `POST /audit/export` (role `audit-admin`) → enqueues + returns `{jobId}`. `GET /audit/export/{jobId}` (role `audit-reader`) → returns status + download URL when ready. Build, commit, push.

5. **PartitionRolloverService** — `BackgroundService` with 24h period. On each tick: ensure next 14 days of partitions exist (effectively current + next month). Idempotent: `CREATE TABLE IF NOT EXISTS …` pattern. Uses `TimeProvider` (registered in L0's Program.cs). Build, commit, push.

6. **Integration tests** — `ExportJobTests`: submit small range, poll status, assert `succeeded` with valid signed URL, download CSV, assert content matches DB query; submit empty range, assert empty CSV (header row only). `PartitionRolloverTests`: skew `TimeProvider` to last day of month, run one tick, assert next month's partition exists in `pg_inherits`; run two ticks, no error. Build, run, commit, push.

7. **Wire DI** — `DependencyInjection.Export.cs` registers `IAuditExportJob`, `AuditExportWorker` as `IHostedService`, `PartitionRolloverService` as `IHostedService`. Commit, push.

**Done:** `dotnet test "$WT/tests/Audit.Integration/Audit.Integration.csproj" -c Release --nologo --filter "FullyQualifiedName~Export|FullyQualifiedName~Partition"` — exit 0.

---

## After all 4 tracks merge

Once all four PRs are merged into `feat/audit-service`, run the full suite once on the integration branch:

```bash
cd /Users/chidionyema/Documents/code/rw-audit
git checkout feat/audit-service
git pull --ff-only origin feat/audit-service

dotnet build deploy/aspire/HaworksPlatform.AppHost.csproj -c Release --nologo --verbosity quiet
dotnet test  tests/Audit.Unit/Audit.Unit.csproj                -c Release --nologo
dotnet test  tests/Audit.Integration/Audit.Integration.csproj  -c Release --nologo
```

All exit 0 → open the rollup PR `feat/audit-service` → `main`.
