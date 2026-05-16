> **Note:** This brief was written when the plan was Meilisearch. The actual implementation uses **Elasticsearch 8**. References to Meilisearch below are historical.

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
"Services__search-svc__http__0" = "http://haworks-search.flycast:8080"
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
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/BffWeb.Integration -c Release       # BFF unit/integration suite
dotnet test tests/Smoke -c Release                    # smoke test compiles
```

All build/test green.

**Then** commit your work locally on the `feat/search/B7` branch, then push **that branch only** so the user can review:

```bash
git push origin feat/search/B7
```

Stop there. **Do not push to main.** **Do not run the Deploy workflow.** **Do not run the smoke test against the deployed BFF.** Those steps belong to the user's release process: they review your branch, merge it into `feat/search-service-spec`, merge that into `main` when satisfied, and the existing Deploy workflow fires automatically. Once deployed, the user runs the BFF-against-staging smoke themselves.

In your done-report, include:
- The exact `dotnet test` outputs from local runs.
- The push output (`git push origin feat/search/B7`) confirming the branch is on origin.
- A short checklist the user should run after merging:
  ```
  # After merging feat/search/B7 into main:
  gh run watch $(gh run list --workflow Deploy --limit 1 --json databaseId -q '.[0].databaseId')
  BFF_BASE_URL=https://haworks-bffweb.fly.dev dotnet test tests/Smoke -c Release \
      --filter "FullyQualifiedName~SearchSmokeTests"
  ```

## Hard stops

- Do **not** modify search-svc code (B5/B6 are done).
- Do **not** modify Meilisearch settings.
- Do **not** add CORS rules or auth scopes to the new BFF route beyond what the existing pattern does (the BFF already has CORS configured globally).
- Do **not** push to `main` or to `feat/search-service-spec`. Push only your `feat/search/B7` branch.
- Do **not** trigger the Deploy workflow. Do **not** run flyctl deploy.
- Do **not** run the staging smoke test (`BFF_BASE_URL=...`) — that's a post-deploy check the user runs.
- Do **not** force-push or skip CI hooks.
- The user has flagged cost concerns — do **not** scale up Meilisearch's machine size or add a second Fly machine to "fix" latency without explicit user approval.

## Done-report

Standard format, plus:
- Confirm the local `dotnet test tests/BffWeb.Integration` and `dotnet test tests/Smoke` outputs (paste the summary lines).
- Paste the `git push origin feat/search/B7` output confirming the branch landed on origin.
- Include the post-merge checklist (gh run watch + BFF smoke) for the user to run themselves.
- "Out-of-scope observations" should explicitly list anything that would benefit from phase 5 hardening (perf, ops runbook, backfill UX).
