# Brief L1.C — Query API

## Goal
Expose `GET /audit/events` (filtered, paginated) and `GET /audit/events/{id}` (single lookup) so support and compliance can read the captured trail.

## Phase / blocks-on
Phase L1.C. Blocks-on: L0 + L1.B committed (need a populated `audit_events` table to query, plus the EF context wired).

## Inputs
1. `docs/agent-briefs/audit/README.md`.
2. `docs/agent-briefs/audit-service-spec.md` — section 3.1 (request/response shape) and section 7 (SLA targets — informs index choices but those are L1.B's responsibility).
3. `src/Audit/Audit.Infrastructure/Persistence/AuditDbContext.cs` — confirm the entity shape.
4. `src/Notifications/Notifications.Api/Controllers/` — match controller style + JWT/role gating pattern.
5. `src/BuildingBlocks/Extensions/ServiceDefaults.cs` — `MapDefaultEndpoints` already mounts health/correlation; controllers go on the standard ASP.NET Core route table.

## Deliverable

### New files
- `src/Audit/Audit.Application/Queries/GetAuditEventsQuery.cs` — `record GetAuditEventsQuery(string? EntityId, string? EntityType, string? EventType, DateTimeOffset From, DateTimeOffset To, int Limit, string? Cursor) : IRequest<GetAuditEventsResponse>`.
- `src/Audit/Audit.Application/Queries/GetAuditEventsQueryHandler.cs` — MediatR handler. EF Core query with filters; cursor decode; LINQ `.Take(limit + 1)` to determine whether a `nextCursor` exists; cursor encoded base64 of `(occurred_at_ticks, id_guid)` tuple.
- `src/Audit/Audit.Application/Queries/GetAuditEventByIdQuery.cs` + handler — single lookup.
- `src/Audit/Audit.Application/Queries/Validation/GetAuditEventsQueryValidator.cs` — FluentValidation: `from < to`, `to - from <= 90 days`, `limit ∈ [1, 1000]`.
- `src/Audit/Audit.Application/Queries/AuditCursor.cs` — internal helper with `Encode(occurredAt, id)` and `TryDecode(string, out DateTimeOffset, out Guid)`. Signed? No — cursors are not auth tokens, just opaque state.
- `src/Audit/Audit.Api/Controllers/AuditController.cs` — `[Authorize(Roles="audit-reader")]`. `GET /audit/events`, `GET /audit/events/{id:guid}`. Returns the response shape from spec § 3.1.
- `tests/Audit.Integration/QueryApiTests.cs`:
  - happy path: seed 25 rows, `GET /audit/events?entityType=order&from=...&to=...&limit=10` returns 10 + cursor; follow cursor → next page.
  - filter-by-eventType.
  - filter-by-entityId.
  - 90-day cap rejection.
  - 401 without JWT, 403 with wrong role.
  - 404 for unknown id.

### Modified files
- `src/Audit/Audit.Api/Program.cs` — `services.AddControllers()` (if not already from L0), `app.MapControllers()`. MediatR scan-from-assembly call (mirror Notifications).
- `src/Audit/Audit.Application/Audit.Application.csproj` — verify FluentValidation reference (already in by Notifications precedent — confirm).
- `src/Audit/Audit.Application/DependencyInjection.cs` — register `IValidator<GetAuditEventsQuery>`.

## Acceptance

```bash
cd /Users/chidionyema/Documents/code/rw-audit

dotnet build src/Audit/Audit.Application/Audit.Application.csproj -c Release --nologo --verbosity quiet
dotnet build src/Audit/Audit.Api/Audit.Api.csproj                 -c Release --nologo --verbosity quiet
dotnet test  tests/Audit.Integration/Audit.Integration.csproj     -c Release --nologo --logger "console;verbosity=minimal"
```

All pass. New tests added (the file should hold 6+ scenarios from above).

Commit:
```bash
git add -A
git commit -m "feat(audit/L1.C): GET /audit/events query API + cursor pagination

90-day range cap, audit-reader role gate, opaque base64 cursor over
(occurred_at, id). Filters by entity, eventType, time range. Read p95
target <100ms validated against the seeded fixture.

Per docs/agent-briefs/audit-service-spec.md § 3.1, § 7."
```

## Hard stops — parallel-scope

L1.C runs in PARALLEL with L1.A / L1.B / L1.D. You touch ONLY these paths:

- `src/Audit/Audit.Application/Queries/**`
- `src/Audit/Audit.Api/Controllers/AuditController.cs` (this file only — L1.D adds a SEPARATE `AuditExportController.cs`)
- `src/Audit/Audit.Application/DependencyInjection.Queries.cs` (fill in the body)
- `tests/Audit.Integration/QueryApiTests.cs`

If you need a change anywhere else, file a blocker.

Plus the standard hard stops:

- Do NOT add the export endpoint or the partition cron — that's L1.D.
- Do NOT change the EF context schema — L1.B owns it.
- Do NOT use offset pagination. Cursor only.
- Do NOT add an admin-write endpoint. Audit is read-only.
- Cursor format is internal; do NOT expose its structure to clients.

## Done-report format

Per `README.md`.
