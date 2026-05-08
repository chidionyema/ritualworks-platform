# B7 — BFF wiring + staging smoke

## Goal

Expose search-svc through the BFF as `GET /api/search?q=…&categoryId=…&page=&pageSize=`. Add a smoke test that hits the deployed BFF endpoint and asserts a 200 with at least one hit. Then run the deploy.

## Phase / blocks-on

Phase 4. Blocks-on: **B5 AND B6** both green and merged. No parallel work in this phase.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §3.1 (HTTP contract — BFF passes through unchanged), §7 (SLA), §11 (failure modes — useful for the smoke test's failure case).
3. `src/BffWeb/BffWeb.Api/Program.cs` — find where service-discovery HttpClients are registered (search for `Services__catalog-svc` or `flycast`). The same pattern applies for `search-svc`.
4. `fly.bffweb.toml` — BFF flycast service-discovery config lives here in the `[env]` block. Add an entry for search.
5. `tests/Smoke/` — find the existing smoke test for one of the services (e.g. catalog) and copy its shape.
6. `.github/workflows/deploy.yml` — confirm B1 left the matrix in the right state (search + meilisearch are in the matrix-builder).

## Deliverable

### BFF route

A controller or minimal API endpoint in BffWeb.Api:

`src/BffWeb/BffWeb.Api/Controllers/SearchController.cs` (or wherever the BFF puts service-proxy endpoints):

- Inject the typed `ISearchClient` (Refit, mirroring how other BFF→service clients are typed).
- `GET /api/search` → forward to search-svc's `GET /search` with the same query string.
- Return 200/400/500 passthrough; no transformation.

### Service discovery for the BFF

In `fly.bffweb.toml`'s `[env]` block (Fly secrets API rejects hyphens — keep this in env, not secrets, like the other services):

```toml
"Services__search-svc__http__0" = "http://ritualworks-search.flycast:8080"
```

In BFF DI:

```csharp
services.AddRefitClient<ISearchClient>()
    .ConfigureHttpClient(c =>
    {
        c.BaseAddress = new Uri(configuration["Services:search-svc:http:0"]
            ?? "http://localhost:5099");
        c.Timeout = TimeSpan.FromSeconds(3);   // BFF tail-latency budget tighter than search-svc internal
    });
```

`ISearchClient` typed interface — same shape as `ICatalogProductsApi` from B4. Lives in `BffWeb.Api/Clients/`.

### Smoke test

`tests/Smoke/SearchSmokeTests.cs`:

```csharp
[Fact]
public async Task BFF_search_returns_200_with_hits()
{
    var bff = Environment.GetEnvironmentVariable("BFF_BASE_URL")
        ?? throw new InvalidOperationException("BFF_BASE_URL not set");
    using var http = new HttpClient { BaseAddress = new Uri(bff), Timeout = TimeSpan.FromSeconds(5) };
    var resp = await http.GetAsync("/api/search?q=test");
    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
    body.GetProperty("hits").GetArrayLength().Should().BeGreaterThan(0,
        "the staging catalog should have at least one product matching 'test'");
}
```

If the existing smoke test infrastructure uses a different harness (e.g. `WebApplicationFactory<Program>` against staging), match that — don't invent a new pattern.

### Local end-to-end check

Add a paragraph to `docs/agent-briefs/search/B7-bff-and-smoke.md`'s done-report (see "Done-report" below) documenting how the agent ran the local end-to-end check before pushing.

## Acceptance

```bash
dotnet build RitualworksPlatform.sln -c Release
dotnet test tests/BffWeb.Integration -c Release       # BFF unit/integration suite
dotnet test tests/Smoke -c Release                    # smoke test compiles
```

All build/test green.

**Then** the agent commits, pushes to main, and watches the Deploy workflow:

```bash
gh run list --workflow Deploy --limit 1
gh run watch <run_id> --exit-status
```

Deploy must finish green (all matrix entries succeed). Then run the smoke against the deployed BFF:

```bash
BFF_BASE_URL=https://ritualworks-bffweb.fly.dev dotnet test tests/Smoke -c Release \
    --filter "FullyQualifiedName~SearchSmokeTests"
```

Must pass.

## Hard stops

- Do **not** modify search-svc code (B5/B6 are done).
- Do **not** modify Meilisearch settings.
- Do **not** add CORS rules or auth scopes to the new BFF route beyond what the existing pattern does (the BFF already has CORS configured globally).
- Do **not** force-push or skip CI hooks to ship.
- If the deploy fails, **stop**. Don't try to flip the matrix or retry blindly. File a blocker with the failed job name and the last 30 lines of its log.
- The user has flagged cost concerns — do **not** scale up Meilisearch's machine size or add a second Fly machine to "fix" latency without explicit user approval.

## Done-report

Standard format, plus:
- Paste the deployed-BFF smoke test output.
- Paste a sample curl: `curl https://<bff-url>/api/search?q=…` and the JSON body.
- Note the deploy run URL.
- "Out-of-scope observations" should explicitly list anything that would benefit from phase 5 hardening (perf, ops runbook, backfill UX).
