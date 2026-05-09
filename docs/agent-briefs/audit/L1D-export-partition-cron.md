# Brief L1.D — Export job + partition cron

## Goal
Two operational concerns rolled into one phase: (a) async CSV export to S3 for compliance dumps, (b) a `BackgroundService` that pre-creates each next-month partition 14 days ahead so inserts never block on schema changes.

## Phase / blocks-on
Phase L1.D. Blocks-on: L0 + L1.B (need the partitioned table + EF context). Order with L1.C is independent; either can be done first.

## Inputs
1. `docs/agent-briefs/audit/README.md`.
2. `docs/agent-briefs/audit-service-spec.md` — section 3.1 (export endpoints), section 4 (partition shape, retention rules).
3. `src/BuildingBlocks/Storage/` — find the existing `Storage` abstraction (used by Content service for S3/Tigris). Match its API.
4. `src/Content/Content.Infrastructure/` — find the upload-side code that already calls Storage; copy the patterns for signed-URL generation.
5. `src/Notifications/` — find any `BackgroundService` patterns (e.g. retry workers); use the same registration shape via `services.AddHostedService<>()`.
6. `src/Audit/Audit.Application/Queries/GetAuditEventsQueryHandler.cs` (from L1.C) — the export reuses the same filter contract.

## Deliverable

### Export

#### New files
- `src/Audit/Audit.Application/Export/AuditExportRequest.cs` — same filter shape as `GetAuditEventsQuery` but no cursor/limit.
- `src/Audit/Audit.Application/Export/IAuditExportJob.cs` — `Task<Guid> EnqueueAsync(AuditExportRequest, CancellationToken)`, `Task<AuditExportStatus> GetStatusAsync(Guid jobId, CancellationToken)`.
- `src/Audit/Audit.Infrastructure/Export/AuditExportJob.cs` — implementation. Uses an in-process `Channel<AuditExportWorkItem>` plus a `BackgroundService` consumer (`AuditExportWorker`). Worker streams rows via EF Core `IAsyncEnumerable`, writes CSV via `CsvHelper` (or hand-rolled — match what BuildingBlocks already references; if neither, use `System.Globalization` + manual escaping), uploads to S3 via the existing Storage abstraction, sets row in `audit_export_jobs` to `succeeded` with the signed URL.
- `src/Audit/Audit.Infrastructure/Persistence/AuditExportJobsTable.cs` — entity + EF Core mapping for a small `audit_export_jobs(id, status, requested_by, request_json, started_at, completed_at, download_url, error)` table. Add to a new migration `<timestamp>_AddAuditExportJobs.cs`.
- `src/Audit/Audit.Api/Controllers/AuditExportController.cs` — `POST /audit/export` (role `audit-admin`), `GET /audit/export/{jobId}` (role `audit-reader`).

#### Tests
- `tests/Audit.Integration/ExportJobTests.cs`:
  - Submit an export with a small range; poll status; assert eventually `succeeded` with a valid signed URL; download the CSV; assert content matches DB query.
  - Submit with empty range; assert `succeeded` with empty CSV (header row only).

### Partition cron

#### New files
- `src/Audit/Audit.Infrastructure/Partitions/PartitionRolloverService.cs` — `class PartitionRolloverService : BackgroundService`. Loop period 24h. On each tick: ensure partitions for the next 14 days exist (effectively the current month and the next month, given monthly partitioning). Idempotent — if it exists, skip; if it doesn't, run the partition-create + index-create SQL from the spec § 4.
- `tests/Audit.Integration/PartitionRolloverTests.cs`:
  - Inject `TimeProvider` to skew clock to last-day-of-month, run one tick, assert next month's partition exists in `pg_inherits`.
  - Run two ticks back-to-back, assert no error (idempotent).

#### Modified files
- `src/Audit/Audit.Api/Program.cs` — `services.AddSingleton<TimeProvider>(TimeProvider.System)`, `services.AddHostedService<AuditExportWorker>()`, `services.AddHostedService<PartitionRolloverService>()`. Add `services.AddSingleton<IAuditExportJob, AuditExportJob>()`.

## Acceptance

```bash
cd /Users/chidionyema/Documents/code/rw-audit

dotnet build src/Audit/Audit.Infrastructure/Audit.Infrastructure.csproj -c Release --nologo --verbosity quiet
dotnet build src/Audit/Audit.Api/Audit.Api.csproj                       -c Release --nologo --verbosity quiet

# Apply migration to a fresh postgres
docker run --rm -d --name audit-pg-test -e POSTGRES_PASSWORD=postgres -p 54329:5432 postgres:16-alpine
sleep 3
ConnectionStrings__audit="Host=localhost;Port=54329;Database=postgres;Username=postgres;Password=postgres" \
  dotnet ef database update -p src/Audit/Audit.Infrastructure -s src/Audit/Audit.Api
docker rm -f audit-pg-test

# Integration tests
dotnet test tests/Audit.Integration/Audit.Integration.csproj -c Release --nologo --logger "console;verbosity=minimal"
```

All pass. If Docker not available, integration tests can be marked TODO; build must still pass.

Commit:
```bash
git add -A
git commit -m "feat(audit/L1.D): export job (CSV→S3) + partition rollover BackgroundService

POST /audit/export returns a job id; poll for the signed-URL CSV output.
PartitionRolloverService keeps the next month's partition pre-created
14 days ahead so inserts never block on schema changes. audit-admin
role gates export.

Per docs/agent-briefs/audit-service-spec.md § 3.1, § 4."
```

## Hard stops — parallel-scope

L1.D runs in PARALLEL with L1.A / L1.B / L1.C. You touch ONLY these paths:

- `src/Audit/Audit.Application/Export/**`
- `src/Audit/Audit.Infrastructure/Export/**`
- `src/Audit/Audit.Infrastructure/Partitions/**`
- `src/Audit/Audit.Infrastructure/Migrations/<timestamp>_AddAuditExportJobs.*` (separate timestamp from L1.B's events migration — don't touch theirs)
- `src/Audit/Audit.Api/Controllers/AuditExportController.cs` (this file only — L1.C owns `AuditController.cs`)
- `src/Audit/Audit.Application/DependencyInjection.Export.cs` (fill in the body)
- `tests/Audit.Integration/ExportJobTests.cs`, `tests/Audit.Integration/PartitionRolloverTests.cs`

The `AuditExportJob` DbSet was already declared in L0's `AuditDbContext`; you do NOT modify the DbContext. You add the migration that creates the table.

If you need a change anywhere else, file a blocker.

Plus the standard hard stops:

- Do NOT introduce Hangfire / Quartz / any other job framework. `Channel<T>` + `BackgroundService` is the pattern.
- Do NOT use Parquet — CSV only at this phase. (Parquet was a stretch goal in the spec; defer.)
- Do NOT change the `audit_events` schema. The export jobs table is a SEPARATE table.
- Do NOT add a "delete export job" endpoint. Cleanup happens via TTL on the export rows + S3 lifecycle policy (operator concern).
- The partition service runs on `TimeProvider.System` so tests can fake the clock; do NOT use `DateTime.UtcNow` directly.

## Done-report format

Per `README.md`.
