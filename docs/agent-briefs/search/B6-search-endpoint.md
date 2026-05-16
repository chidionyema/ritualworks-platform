> **Note:** This brief was written when the plan was Meilisearch. The actual implementation uses **Elasticsearch 8**. References to Meilisearch below are historical.

# B6 — GET /search endpoint

## Goal

Public-shape `GET /search` endpoint on search-svc. Accepts query, optional categoryId, page, pageSize. Calls Meilisearch via `ISearchIndex`. Returns the Gemini-friendly envelope from spec §3.1.

## Phase / blocks-on

Phase 3. Blocks-on: B2 done (`ISearchIndex` exists and `EnsureSettingsAsync` works). **Independent of B5** — can run in parallel; B5 and B6 don't share files.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §3.1 (HTTP contract — copy the response shape verbatim) and §9.2 (test list — every test name listed there must exist and pass).
3. `src/Search/Search.Application/Interfaces/ISearchIndex.cs` (B2).
4. `src/Search/Search.Application/Models/` — the `SearchQuery` and `SearchPage` POCOs from B2.
5. `src/Catalog/Catalog.Api/Controllers/` — pick one existing controller as a style template (logger, scope, route attributes, model validation).
6. `tests/Catalog.Integration/` — pick one HTTP test as the WebApplicationFactory invocation template.

## Deliverable

### Endpoint

`src/Search/Search.Api/Controllers/SearchController.cs`:

```csharp
[ApiController]
[Route("search")]
public sealed class SearchController(ISearchIndex index, ILogger<SearchController> logger)
    : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery, Required, MinLength(1), MaxLength(200)] string q,
        [FromQuery] Guid? categoryId,
        [FromQuery, Range(1, 10_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var query = new SearchQuery
        {
            Query = SearchQuerySanitizer.Sanitize(q),
            CategoryFilter = categoryId,
            Page = page,
            PageSize = pageSize,
        };
        var result = await index.SearchAsync(query, ct);
        sw.Stop();

        return Ok(new SearchResponse
        {
            Query      = q,
            CategoryId = categoryId,
            Page       = page,
            PageSize   = pageSize,
            TotalHits  = result.TotalHits,
            TookMs     = sw.ElapsedMilliseconds,
            Hits       = result.Hits.Select(SearchHitMapper.ToResponse).ToArray(),
        });
    }
}
```

### Helpers

`src/Search/Search.Application/Indexing/SearchQuerySanitizer.cs` — strip control chars, collapse whitespace, trim, max 30 terms (Meilisearch performance cliff).

`src/Search/Search.Api/Mapping/SearchHitMapper.cs` — `Hit` (internal) → `SearchHitResponse` (public). Public DTO matches spec §3.1 field names exactly (camelCase JSON via the global JSON settings).

`src/Search/Search.Application/Models/SearchResponse.cs` — public response shape, JsonSerializerDefaults.Web.

### Wire

In `Search.Api/Program.cs`: `builder.Services.AddControllers(); …; app.MapControllers();`.

Note: `ISearchIndex.SearchAsync` already exists from B2 — it must be the implementation that calls Meili's search. If B2 left `SearchAsync` as a stub `throw new NotImplementedException()`, that's a B2 bug — file a blocker, do not implement Meili search logic in B6.

### Tests

`tests/Search.Unit/SearchQuerySanitizerTests.cs`:
- `Sanitize_strips_control_chars`
- `Sanitize_collapses_whitespace`
- `Sanitize_caps_at_30_terms`
- `Sanitize_returns_empty_for_pure_whitespace_input`

`tests/Search.Integration/SearchEndpointTests.cs` (uses `SearchWebAppFactory` from B2 with seeded Meili docs):
- `Search_returns_paged_hits_for_known_term` — seed 25 docs, search a common word, expect pageSize=20 + page 1, totalHits=25.
- `Search_filters_by_category` — seed docs in 2 categories, filter one, assert only that category returned.
- `Search_returns_400_when_q_empty` — empty `q` → 400.
- `Search_returns_400_when_pageSize_over_100` → 400.
- `Search_handles_typos_via_meilisearch` — seed doc "Headphones", query "headfones", expect at least 1 hit (Meilisearch's built-in typo tolerance, no special config needed).
- `Search_returns_within_p99_target` — single hot query, assert response `tookMs < 100`. Soft assert with a logged warning if it slips, fail at >250ms (CI variance).

## Acceptance

```bash
dotnet build RitualworksPlatform.sln -c Release
dotnet test tests/Search.Unit -c Release
dotnet test tests/Search.Integration -c Release
```

All green. The 4 new unit + 6 new integration tests pass.

## Hard stops

- Do **not** implement Meilisearch search logic here. `ISearchIndex.SearchAsync` is B2's territory.
- Do **not** add authentication / authorization to the endpoint in v1. The endpoint is internal-only (flycast); the BFF in B7 handles user-facing auth.
- Do **not** add a `/search/health` or `/admin/reindex` endpoint — out of scope.
- Do **not** invent response fields beyond spec §3.1.
- Do **not** expose Meilisearch's master key, raw Meili response shape, or stack traces in 4xx/5xx responses.

## Done-report

Standard format. Confirm:
- All 10 new tests pass.
- The response body fields and field casing match spec §3.1 exactly (paste a sample response in the report).
- p99 test passed and the actual `tookMs` value (so we have a baseline).
