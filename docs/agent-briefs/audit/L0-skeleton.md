# Brief L0 — Audit service skeleton + DI + Aspire wiring

## Goal
Create the four `Audit.*` projects, register them in the solution, wire the service into Aspire and compose, **and lay the DI foundation that lets L1.A / L1.B / L1.C / L1.D run in parallel**. No business logic — empty consumers, empty controllers, empty extractors. Audit needs to boot before L1.A/L1.B/L1.C/L1.D fill it in, and the four follow-ups must never touch each other's files.

## Phase / blocks-on
Phase L0. Blocks-on: none. Branch: `feat/audit-service` already exists in this worktree.

## Inputs (read in this order, in full)

1. `docs/agent-briefs/audit/README.md` — protocol.
2. `docs/agent-briefs/audit-service-spec.md` — sections 1, 2, 4 (data model — for the DbContext shape), 8 (topology & deployment).
3. `src/Notifications/` — full directory listing first, then read `Notifications.Api/Notifications.Api.csproj`, `Notifications.Api/Program.cs`, `Notifications.Api/Dockerfile`. This is the closest pattern to copy.
4. `src/Notifications/Notifications.Domain/Notifications.Domain.csproj` and `…/Notifications.Application/…csproj` and `…/Notifications.Infrastructure/…csproj` — match the same shape.
5. `src/BuildingBlocks/Extensions/ServiceDefaults.cs` — confirm what `AddServiceDefaults()` and `MapDefaultEndpoints()` provide.
6. `deploy/aspire/Program.cs` — find where `var notifications = builder.AddProject<Projects.Notifications_Api>("notifications-svc")` is wired; the audit registration sits next to it.
7. `deploy/aspire/init-postgres.sql` — confirm the `CREATE DATABASE` block; `audit` must be added.
8. `deploy/compose/docker-compose.yml` — find `notifications-svc:`; the audit entry mirrors it.
9. `RitualworksPlatform.sln` — confirm where Notifications projects are listed; Audit projects go in the same group.

## Deliverable

### New project files

#### Domain
- `src/Audit/Audit.Domain/Audit.Domain.csproj` — references `Microsoft.EntityFrameworkCore.Abstractions` only if needed; otherwise empty.
- `src/Audit/Audit.Domain/AuditEvent.cs` — minimal entity with the columns from spec § 4 (id, occurred_at, received_at, event_type, entity_type, entity_id, actor_id, actor_type, correlation_id, payload, metadata). EF Core-friendly (private setters OK; nothing fancy).

#### Application — interfaces and DI scaffolding
The interfaces here MUST be complete signatures (not stubs) — L1.A/B/C/D rely on them being stable.
- `src/Audit/Audit.Application/Audit.Application.csproj` — references Audit.Domain + Haworks.Contracts + MediatR + FluentValidation. Add ALL packages the four L1 phases will need so none of them has to modify the csproj: MediatR, FluentValidation, FluentValidation.DependencyInjectionExtensions, Microsoft.Extensions.Hosting.Abstractions.
- `src/Audit/Audit.Application/Extraction/IAuditExtractor.cs` — full interface per spec § 5.1: `Task<AuditRow> Extract<T>(T evt, ConsumeContext ctx)` (or the closed-generic shape the spec uses).
- `src/Audit/Audit.Application/Extraction/AuditRow.cs` — full record per spec § 5.1.
- `src/Audit/Audit.Application/Redaction/ISecretRedactor.cs` — `JsonElement Redact(JsonElement input)`.
- `src/Audit/Audit.Application/Capture/IAuditConsumerRegistry.cs` — `void RegisterConsumers(IBusRegistrationConfigurator cfg)` interface (no impl yet — L1.B fills it).
- `src/Audit/Audit.Application/Export/IAuditExportJob.cs` — full interface per L1.D brief.
- `src/Audit/Audit.Application/Export/AuditExportRequest.cs` and `AuditExportStatus.cs` records.
- `src/Audit/Audit.Application/DependencyInjection.cs` — top-level extension `AddAuditApplication(this IServiceCollection)` that calls all four phase-specific extensions:
  ```csharp
  public static IServiceCollection AddAuditApplication(this IServiceCollection s) =>
      s.AddAuditExtractors().AddAuditCapture().AddAuditQueries().AddAuditExport();
  ```
- `src/Audit/Audit.Application/DependencyInjection.Extractors.cs` — empty stub: `public static IServiceCollection AddAuditExtractors(this IServiceCollection s) => s;` (L1.A fills body).
- `src/Audit/Audit.Application/DependencyInjection.Capture.cs` — empty stub. (L1.B fills body.)
- `src/Audit/Audit.Application/DependencyInjection.Queries.cs` — empty stub. (L1.C fills body.)
- `src/Audit/Audit.Application/DependencyInjection.Export.cs` — empty stub. (L1.D fills body.)

These four sibling DI files are how parallelism works — each L1 phase owns ONE of them and never touches the others.

#### Infrastructure
- `src/Audit/Audit.Infrastructure/Audit.Infrastructure.csproj` — references Audit.Application + Npgsql + EF Core Postgres provider + the existing Vault interceptor in BuildingBlocks (match Notifications.Infrastructure). Add `Microsoft.AspNetCore.App` framework reference for `BackgroundService`.
- `src/Audit/Audit.Infrastructure/Persistence/AuditDbContext.cs` — `DbSet<AuditEvent> AuditEvents` (and a stub `DbSet<AuditExportJob>` mapped to `audit_export_jobs` — even though L1.D owns the table, declaring the DbSet here keeps the DbContext immutable across phases). No migrations yet — both L1.B and L1.D add their own.
- `src/Audit/Audit.Infrastructure/Persistence/IAuditWriter.cs` — full interface signature: `ValueTask WriteAsync(AuditRow row, CancellationToken ct)` plus `Task FlushAsync(CancellationToken ct)`.

#### Api
- `src/Audit/Audit.Api/Audit.Api.csproj` — references the Application + Infrastructure projects + BuildingBlocks. Mirror Notifications.Api.csproj package list — and add the test project references the integration test project will need (CsvHelper if needed for L1.D — check existing usage first; if absent, leave for L1.D).
- `src/Audit/Audit.Api/Program.cs` — `builder.AddServiceDefaults()`, `builder.Services.AddDbContext<AuditDbContext>(...)`, `services.AddAuditApplication()` (calls all 4 phase extensions, currently no-ops), `services.AddControllers()`, `services.AddMassTransit(b => { /* L1.B's IAuditConsumerRegistry plugs here */ })`, `app.MapDefaultEndpoints()`, `app.MapControllers()`, `app.Run()`. Configure MassTransit so L1.B can register consumers via the registry interface without touching this file.
- `src/Audit/Audit.Api/Dockerfile` — copy `src/Notifications/Notifications.Api/Dockerfile` and substitute paths.
- `src/Audit/Audit.Api/appsettings.json` — copy `src/Orders/Orders.Api/appsettings.json` (Serilog + JWKS), change `"Application": "audit-svc"`.

#### Test project skeletons (empty — L1.A and L1.B fill them in)
- `tests/Audit.Unit/Audit.Unit.csproj` — xUnit + FluentAssertions + Audit.Application reference. Empty class file `Placeholder.cs` so the project compiles. No tests yet.
- `tests/Audit.Integration/Audit.Integration.csproj` — xUnit + FluentAssertions + Microsoft.AspNetCore.Mvc.Testing + Testcontainers + Audit.Api reference + BuildingBlocks.Testing reference. Empty `Placeholder.cs`.
- `tests/Audit.Integration/AuditWebAppFactory.cs` — `WebApplicationFactory<Program>` mirroring `tests/Notifications.Integration/NotificationsWebAppFactory.cs`; uses `SharedTestPostgres.CreateDatabaseAsync("audit")`. **L1.B will refine this** but the skeleton must boot.

### Modified files
- `RitualworksPlatform.sln` — add the 4 Audit projects + 2 test projects (Unit, Integration) to the same solution folder as Notifications. **L1.A and L1.B do NOT touch the .sln.**
- `deploy/aspire/Program.cs` — add `var auditDb = postgres.AddDatabase("audit");` and the `audit-svc` project registration mirroring `notifications-svc`. Wire OTLP env (`tempo.GetEndpoint("grpc")`), JWKS via `AddJwksConfig`, RabbitMQ + auditDb refs.
- `deploy/aspire/init-postgres.sql` — add `audit` to the CREATE DATABASE iterator (or as a new block, matching the existing style).
- `deploy/compose/docker-compose.yml` — add `audit-svc` mirroring `notifications-svc`.

## Acceptance

```bash
cd /Users/chidionyema/Documents/code/rw-audit

# Each Audit project builds in isolation
dotnet build src/Audit/Audit.Domain/Audit.Domain.csproj         -c Release --nologo --verbosity quiet
dotnet build src/Audit/Audit.Application/Audit.Application.csproj -c Release --nologo --verbosity quiet
dotnet build src/Audit/Audit.Infrastructure/Audit.Infrastructure.csproj -c Release --nologo --verbosity quiet
dotnet build src/Audit/Audit.Api/Audit.Api.csproj               -c Release --nologo --verbosity quiet

# AppHost compiles (verifies Aspire wiring)
dotnet build deploy/aspire/RitualworksPlatform.AppHost.csproj   -c Release --nologo --verbosity quiet

# Solution metadata is valid
dotnet sln list | grep -c "Audit\." | awk '$1==4{exit 0} {exit 1}'

# Compose file parses
docker compose -f deploy/compose/docker-compose.yml config --quiet
```

All must exit 0.

Commit:
```bash
git add -A
git commit -m "feat(audit/L0): skeleton — projects, EF context, Aspire/compose wiring

Empty MassTransit consumers, empty controllers — boots in Aspire alongside
the other services. Next: L1.A extractors + redactor.

Per docs/agent-briefs/audit-service-spec.md."
```

## Hard stops

- Do NOT add any extractor / redactor / consumer logic — those are later phases.
- Do NOT create EF Core migrations — L1.B owns the partitioned-table migration.
- Do NOT touch BuildingBlocks. If audit needs a building-block change, file a blocker.
- Do NOT create `tests/Audit.*` projects yet — each test project is created by the phase that needs it (L1.A → `tests/Audit.Unit`, L1.B → `tests/Audit.Integration`).
- Do NOT add fly.audit.toml — that's outside the in-scope build (the operator can add it later via the existing fly pattern; not blocking).
- Do NOT do a solution-wide build (`dotnet build RitualworksPlatform.sln`).

## Done-report format

Paste the template from `README.md`, filled in.
