# Contributing

## Quick reference

| What | Where |
|---|---|
| Branch from | `main` |
| PR target | `main` |
| CI checks | Build, unit, arch guards, contract, integration (13 suites) |
| Auto-review | Claude PR Review posts findings on every PR |

## Workflow

1. **Branch** — `git checkout -b feat/my-feature main`
2. **Code** — follow conventions below
3. **Test** — run locally before pushing (see pre-push checklist)
4. **Push** — `git push origin feat/my-feature`
5. **PR** — open against `main`; CI + Claude review run automatically
6. **Address feedback** — fix any CI failures or review comments
7. **Merge** — squash merge after approval

## Pre-push checklist

```bash
# 1. Full build (0 errors required)
dotnet build HaworksPlatform.sln

# 2. Architecture guards pass
dotnet test tests/Platform.ArchitecturalGuards/

# 3. Your service's integration tests pass
dotnet test tests/{YourService}/{YourService}.Integration/

# 4. Architecture enforcement script
./scripts/check-architecture.sh
```

## Project structure

Each service follows Clean Architecture with four layers:

```
src/{Service}/
  {Service}.Domain/          # Entities, value objects, domain events
  {Service}.Application/     # Use cases, handlers, DTOs
  {Service}.Infrastructure/  # EF Core, MassTransit, external integrations
  {Service}.Api/             # ASP.NET Core host, DI wiring
```

Each layer has a `DependencyInjection.cs` with `Add{Layer}()` extension methods.

## Coding conventions

- **Error handling** — use `Result<T>` from BuildingBlocks, not raw exceptions
- **Auth** — every state-changing endpoint needs `[Authorize]`; user identity from JWT claims only
- **Database** — EF Core 9 with explicit migrations; each service owns its schema; no cross-schema joins
- **Messaging** — produce events via transactional outbox; consume with idempotent handlers (check inbox)
- **Resilience** — wrap external HTTP calls in Polly policies
- **Observability** — `ILogger<T>` with structured properties; propagate OpenTelemetry context
- **Events** — domain events go in `src/Contracts/`, implement `IDomainEvent`, extend `DomainEvent`; use `{ get; init; }` properties (never positional records)

## Testing rules

- **Shared containers only** — use `SharedTestPostgres`, `SharedTestElasticsearch`, `SharedTestPostGIS` from `BuildingBlocks.Testing.Containers`. Never instantiate raw `PostgreSqlBuilder` or `ContainerBuilder` — CI will fail.
- **Schema-prefix all SQL** — if the DbContext uses `HasDefaultSchema("xxx")`, raw SQL must use `xxx.table_name`
- **No `EnsureDeletedAsync`** — it drops the entire database
- **`ConfigureTestServices`** not `ConfigureServices` — test overrides must run after app DI

## Adding a new service

1. Create the four-layer structure under `src/{NewService}/`
2. Add `DependencyInjection.cs` per layer
3. Register domain events in `src/Contracts/`
4. Add database in `deploy/aspire/Program.cs`
5. Add integration test project in `tests/{NewService}/{NewService}.Integration/`
6. Add the suite to CI matrix in `.github/workflows/ci.yml`
7. Create `fly.newsvc.toml` for production deployment

## Common pitfalls

| Pitfall | What to do instead |
|---|---|
| `$""` with `ExecuteSqlRawAsync` | EF treats `{var}` as parameters — use `Array.Empty<object>()` overload |
| Raw SQL without schema prefix | Always qualify: `audit.audit_events`, not `audit_events` |
| `SELECT *` for concurrency tokens | Use `SELECT *, xmin` — `*` excludes system columns |
| Positional records for events | MassTransit `Init<T>` faults — use `{ get; init; }` properties |
| `s.Delay` on MassTransit schedule | Requires `MessageSchedulerContext` — pass delay inline instead |

## Fast builds

Use solution filters instead of building the full solution:

```bash
dotnet build filters/Payments.slnf   # ~15s vs ~90s
```

## Questions?

Open an issue or check [docs/INDEX.md](docs/INDEX.md) for documentation navigation.
