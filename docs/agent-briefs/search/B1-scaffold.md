> **Note:** This brief was written when the plan was Meilisearch. The actual implementation uses **Elasticsearch 8**. References to Meilisearch below are historical.

# B1 — Scaffold search-svc + Meilisearch Fly plumbing

## Goal

Stand up the empty `search-svc` microservice (4-project layout matching catalog) plus the Fly app + bootstrap.sh + deploy.yml entries for both `haworks-search` and `haworks-meilisearch`, with empty test projects that pass.

## Phase / blocks-on

Phase 1. Blocks all other briefs.

## Inputs (read in order, all of them, before writing)

1. `docs/agent-briefs/search/README.md` — the protocol you're working under.
2. `docs/agent-briefs/search-service-spec.md` — full spec; pay attention to §2 (architecture), §8 (deployment), §12 (phase plan).
3. `src/Catalog/Catalog.Domain/Catalog.Domain.csproj` and the four sibling `Catalog.*.csproj` files — these are your csproj reference templates.
4. `src/Catalog/Catalog.Api/Program.cs` — Program.cs reference shape.
5. `src/Catalog/Catalog.Api/Dockerfile` — Dockerfile reference.
6. `fly.catalog.toml` — fly.toml reference.
7. `deploy/fly/bootstrap.sh` — pay attention to the `INTERNAL_APPS` array, the JWT_SIGNING_KEY_PEM auto-generation block (you'll mirror this for `MEILI_MASTER_KEY`), and the per-app DB-string loop.
8. `.github/workflows/deploy.yml` — the `plan` job's matrix-builder is what you'll update.
9. `tests/Catalog.Unit/Catalog.Unit.csproj` and `tests/Catalog.Integration/Catalog.Integration.csproj` — test project reference shape.
10. `Directory.Build.props` (root) — central package versions; new csproj files inherit from this. Do not redeclare versions here.

## Deliverable

Create the following files. **Do not create or modify anything else.**

### Source projects (all under `src/Search/`)

- `src/Search/Search.Domain/Search.Domain.csproj` — empty class library, references `BuildingBlocks`.
- `src/Search/Search.Application/Search.Application.csproj` — empty class library, references Search.Domain + Contracts.
- `src/Search/Search.Infrastructure/Search.Infrastructure.csproj` — empty class library, references Search.Application + standard infra packages already used by Catalog.Infrastructure (EF, MassTransit.RabbitMQ, etc.). Do **not** add Meilisearch SDK yet — that's B2.
- `src/Search/Search.Api/Search.Api.csproj` — Web SDK project, references Search.Infrastructure + Swashbuckle.
- `src/Search/Search.Api/Program.cs` — minimal: `WebApplication.CreateBuilder`, `AddServiceDefaults()`, register an empty `MapGet("/health", ...)` returning 200, `app.Run()`. Plus `public partial class Program { }` so WebApplicationFactory can pick it up.
- `src/Search/Search.Api/appsettings.json` — empty JSON `{}`.
- `src/Search/Search.Api/Dockerfile` — clone `src/Catalog/Catalog.Api/Dockerfile`, swap `Catalog` for `Search` in paths and assembly names. Keep multi-stage shape verbatim.

### Test projects

- `tests/Search.Unit/Search.Unit.csproj` — packages match Catalog.Unit. References Search.Domain, Search.Application, BuildingBlocks.Testing.
- `tests/Search.Unit/SmokeTest.cs` — one test: `Assert.True(true)` so the runner has something to discover.
- `tests/Search.Integration/Search.Integration.csproj` — packages match Catalog.Integration. References Search.Api + the linked `TestModuleInitializer.cs`.
- `tests/Search.Integration/SearchWebAppFactory.cs` — a `WebApplicationFactory<Program>` subclass implementing `IAsyncLifetime` (mirror `tests/Payments.Integration/PaymentsWebAppFactory.cs`'s shape). For B1 it has empty `InitializeAsync`/`DisposeAsync` and only sets `ASPNETCORE_ENVIRONMENT=Test` before the host builds. Subsequent briefs (B2, B5) extend it with Meili + WireMock containers.
- `tests/Search.Integration/SmokeTest.cs` — one test, uses `IClassFixture<SearchWebAppFactory>`, calls `GET /health`, asserts 200.

### Fly + ops

- `fly.search.toml` — clone `fly.catalog.toml`, then **set the `[http_service]` block to `auto_stop_machines = "off"`, `min_machines_running = 1`** (per spec §7 — no cold starts allowed). Change app name to `haworks-search`, dockerfile path to `src/Search/Search.Api/Dockerfile`, add `[env] Meilisearch__Url = "http://haworks-meilisearch.flycast:7700"`.
- `fly.meilisearch.toml` — see spec §8 for the exact content. Use `getmeili/meilisearch:v1.10`. Volume name `meili_data`, mount `/meili_data`, initial_size 1gb.

### bootstrap.sh additions (modifications, not new file)

In `deploy/fly/bootstrap.sh`:
- Add `haworks-search` and `haworks-meilisearch` to `INTERNAL_APPS`.
- Mirror the existing `JWT_SIGNING_KEY_PEM` auto-generation block to auto-generate `MEILI_MASTER_KEY` (32 bytes urandom, base64-encoded) on first run, persisting to `.env.local`. Stage it as a Fly secret on **both** `haworks-search` (`Meilisearch__MasterKey`) and `haworks-meilisearch` (`MEILI_MASTER_KEY`).
- Guard the per-app DB-connection-string loop so it skips `haworks-meilisearch` (no Postgres dependency).
- Volume creation: after app creation, run `flyctl volumes create meili_data --size 1 --region iad -a haworks-meilisearch` if a `meili_data` volume doesn't already exist on that app. Use `flyctl volumes list -a haworks-meilisearch` to check.

### deploy.yml additions

Modify the `plan` job's matrix-builder to include `"search"` and `"meilisearch"` in **both** branches (the DEPLOY_CONTENT-true and false paths). They deploy on every push.

## Acceptance

Run all of these. All must pass.

```bash
# 1. Solution builds clean
dotnet build HaworksPlatform.sln -c Release

# 2. Unit + integration test projects exist and pass (smoke tests only)
dotnet test tests/Search.Unit -c Release
dotnet test tests/Search.Integration -c Release

# 3. Fly toml validates
flyctl config validate -c fly.search.toml
flyctl config validate -c fly.meilisearch.toml

# 4. bootstrap.sh syntax is valid (don't run it, just lint)
bash -n deploy/fly/bootstrap.sh
```

## Hard stops

- Do **not** add the Meilisearch .NET SDK package to any csproj. That's B2.
- Do **not** create a DbContext, repositories, consumers, or controllers in Search.* projects. Just the bare scaffold.
- Do **not** add any logic to Program.cs beyond the health endpoint.
- Do **not** modify Catalog code, BFF code, or any other service. Only the new search files + bootstrap.sh + deploy.yml.
- Do **not** edit `Directory.Build.props` to add packages. New csproj files inherit. If you find you "need" a new central package version, file a blocker.
- Do **not** auto-deploy. The user pushes manually after review.

## Done-report

Use the format from `docs/agent-briefs/search/README.md`. Specifically confirm:
- All 4 acceptance commands passed.
- The number of new files created matches the deliverable list (count it).
- bootstrap.sh still runs cleanly when sourced (`bash -n` passes).
