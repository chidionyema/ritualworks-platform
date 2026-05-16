# B2 — Elasticsearch typed client + index settings bootstrap

## Goal

Wire a typed `IElasticsearchClient` into `Search.Infrastructure`, plus an idempotent "ensure-index-settings" startup task that creates the `products` index with the schema from spec §4 and applies the settings every cold start. No consumer, no /search endpoint — just the client + bootstrap.

## Phase / blocks-on

Phase 2. Blocks-on: B1 done and merged.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §4 (data model, Elasticsearch index settings) — the `searchableAttributes`, `filterableAttributes`, `sortableAttributes`, `rankingRules`, `typoTolerance` blocks are the exact values you must apply.
3. `src/Search/Search.Infrastructure/Search.Infrastructure.csproj` (created by B1).
4. `src/Search/Search.Api/Program.cs` (created by B1).
5. `src/Catalog/Catalog.Infrastructure/DependencyInjection.cs` — read this to mimic the `AddInfrastructure(configuration, env)` shape and the options-binding pattern.
6. `Directory.Build.props` — confirm whether `Elasticsearch` is centrally pinned. If not, add it (this is the one exception to B1's "no central versions" rule — ask the reviewer first via blocker if unsure).

## Deliverable

### Add the SDK package

Add `Elasticsearch` (the official .NET client, latest stable on NuGet — pin in `Directory.Build.props`) to `src/Search/Search.Infrastructure/Search.Infrastructure.csproj`.

### Options class

`src/Search/Search.Infrastructure/Options/ElasticsearchOptions.cs`:

```csharp
public sealed class ElasticsearchOptions
{
    public const string SectionName = "Elasticsearch";
    [Required] public string Url { get; init; } = "";
    [Required] public string MasterKey { get; init; } = "";
    public string IndexName { get; init; } = "products";
}
```

Bind in DI with `AddOptions<ElasticsearchOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`.

### Typed client wrapper

`src/Search/Search.Application/Interfaces/ISearchIndex.cs` — small interface so consumers/endpoints can depend on it without binding to the concrete SDK:

```csharp
public interface ISearchIndex
{
    Task UpsertAsync(IReadOnlyCollection<ProductSearchDocument> docs, CancellationToken ct = default);
    Task DeleteAsync(string productIdKey, CancellationToken ct = default);
    Task<ProductSearchDocument?> GetAsync(string productIdKey, CancellationToken ct = default);
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default);
    Task EnsureSettingsAsync(CancellationToken ct = default);
}
```

`ProductSearchDocument`, `SearchQuery`, `SearchPage` — POCOs in `Search.Application/Models/`. Field names match spec §4 verbatim. SearchPage = `{ IReadOnlyList<Hit> Hits, int TotalHits, long TookMs }`. Hit = same shape as the §3.1 response `hits[]` element.

`src/Search/Search.Infrastructure/Elasticsearch/ElasticsearchIndex.cs` — implements `ISearchIndex` using the SDK. `EnsureSettingsAsync` calls `index.UpdateSettings(...)` with the exact blob from spec §4. Idempotent: running twice is a no-op.

### Bootstrap on startup

In `Search.Api/Program.cs`, after the host is built and **before** `app.Run()`, resolve `ISearchIndex` and call `EnsureSettingsAsync()` once. Wrap in `try/catch + log warning` so a transiently down Elasticsearch doesn't crash the app boot — Meili comes up alongside search-svc on first deploy.

### DI wiring

`src/Search/Search.Infrastructure/DependencyInjection.cs`:

```csharp
public static IServiceCollection AddInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration,
    IHostEnvironment env)
{
    services.AddOptions<ElasticsearchOptions>()
        .Bind(configuration.GetSection(ElasticsearchOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddSingleton<ElasticsearchClient>(sp =>
    {
        var opt = sp.GetRequiredService<IOptions<ElasticsearchOptions>>().Value;
        return new ElasticsearchClient(opt.Url, opt.MasterKey);
    });
    services.AddScoped<ISearchIndex, ElasticsearchIndex>();

    return services;
}
```

Call from Program.cs.

### Tests

`tests/Search.Integration/ElasticsearchIndexTests.cs`:

- Use Testcontainers with `getmeili/elasticsearch:v1.10` (set `MEILI_MASTER_KEY=test_master_key`, `MEILI_NO_ANALYTICS=true`).
- Test `EnsureSettingsAsync_creates_index_with_expected_settings`: call once, assert `searchableAttributes` etc. match spec §4 (read settings back via SDK).
- Test `EnsureSettingsAsync_is_idempotent`: call twice, assert no error.
- Test `Upsert_then_Get_roundtrips_a_document`.
- Test `Delete_removes_a_document`.
- Test `SearchAsync_returns_seeded_doc_for_term_in_name` — seed one doc, call `SearchAsync(new SearchQuery { Query = "<word from name>" })`, assert TotalHits == 1 and the hit's productId matches. **B6 depends on this method working** — without this test, B6 may discover the implementation is a stub.
- **Create or extend** `tests/Search.Integration/SearchWebAppFactory.cs` — if B1 created an empty stub, extend it; otherwise create it from scratch as a `WebApplicationFactory<Program>, IAsyncLifetime` mirroring `tests/Payments.Integration/PaymentsWebAppFactory.cs`'s shape. Spin up the Meili container in `InitializeAsync` and set `Elasticsearch__Url` + `Elasticsearch__MasterKey` env vars **before** `base.CreateHost(...)` is invoked — same pattern as `PaymentsWebAppFactory.InitializeAsync()`. Update `tests/Search.Integration/SmokeTest.cs` (created by B1, currently using `WebApplicationFactory<Program>` directly) to use `IClassFixture<SearchWebAppFactory>` instead.

`tests/Search.Unit/` — no new tests for B2 (the wrapper is too thin to unit-test meaningfully; integration is the right level).

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Search.Unit -c Release
dotnet test tests/Search.Integration -c Release
```

All green. Specifically the 4 new tests in `ElasticsearchIndexTests.cs` are listed as passed.

## Hard stops

- Do **not** add a `/search` endpoint — that's B6.
- Do **not** add MassTransit consumers — that's B5.
- Do **not** call out to catalog HTTP — that's B4.
- Do **not** invent a `ProductSearchDocument` field beyond what spec §4 lists.
- Do **not** add EF / DbContext code; v1 has no Postgres dependency in search-svc.
- If the Elasticsearch SDK API differs from this brief's signatures, prefer the SDK's actual API and note the deviation in "out-of-scope observations" — don't fight the SDK.

## Done-report

Standard format. Confirm:
- 4 new tests pass (list their names).
- `EnsureSettingsAsync` is called from Program.cs and gracefully tolerates Meili being down.
- The exact `searchableAttributes`/`filterableAttributes`/etc. from spec §4 are what you applied.
