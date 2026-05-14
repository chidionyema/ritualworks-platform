# Search Service — End-to-End Spec

**Status:** signed off 2026-05-08 — engine = Elasticsearch, topology = single machine, category events = yes, Gemini API = deferred to v2
**Implementer:** Gemini CLI agents working brief-by-brief from `docs/agent-briefs/search/`
**Reviewer:** Claude / user, between phases
**Target:** ship v1 behind the BFF, ready for Gemini-powered re-ranking in v2

---

## 1. Goal & non-goals

**Goal.** A new `search-svc` microservice that indexes catalog products and serves low-latency, highly-available keyword search. Three query shapes for v1:

- `GET /search?q=<text>&page=&pageSize=` — free-text across all listed products
- `GET /search?q=<text>&categoryId=<guid>&page=&pageSize=` — same, scoped to a category
- `POST /search/saved` — register a "percolator" query for reverse search (saved search alerts)

The "for Gemini" framing means the result envelope is shaped to feed an LLM (stable JSON, `score`, snippet fields) so a future `assistant-svc` can pass results to Gemini as context without a translation layer. **Gemini calls themselves are out of scope for v1.**

**Non-goals (v1):**
- Vector / semantic search (deferred to v2; engine choice keeps this door open)
- Faceted filtering by attributes (price, stock) — pageable and filter-by-category only
- Multi-tenant isolation
- Personalization / user-specific ranking
- Search analytics dashboard

---

## 2. Architecture at a glance

```
                                    ┌───────────────────────────┐
   user → BFF /search → flycast →   │  search-svc (Fly, 1 vm)   │
                                    │  ─────────────────────    │
                                    │  • Search.Api             │
                                    │  • Search.Application     │
                                    │  • Search.Infrastructure  │
                                    │  • Search.Domain          │
                                    └────────────┬──────────────┘
                                                 │ Elasticsearch SDK
                                                 │ (HTTP, flycast)
                                                 ▼
                                    ┌───────────────────────────┐
                                    │ ritualworks-elasticsearch │
                                    │ (Fly, 1 vm + data volume) │
                                    │ index = "products"        │
                                    │ index = "saved_searches"  │
                                    └────────────▲──────────────┘
                                                 │ _bulk / _search
                                                 │
   catalog-svc ─ outbox ─ RabbitMQ ─►  ProductCacheInvalidatedEvent (existing)
                                       CategoryUpdatedEvent (NEW, see §6)
                                       CategoryDeletedEvent (NEW)
                                                 │
                                                 ▼
                                    ┌───────────────────────────┐
                                    │  Search.Application       │
                                    │  Indexer (MT consumer)    │
                                    │  ─────────────────────    │
                                    │  • on event → fetch       │
                                    │    product via            │
                                    │    catalog flycast HTTP   │
                                    │  • map → Meili document   │
                                    │  • addDocuments (upsert)  │
                                    └───────────────────────────┘
```

**Why Meilisearch.** Best-in-class typo tolerance and relevance out of the box, no FTS tuning required, Rust-fast (sub-30ms p99 for 100k–1M docs on a small VM), tiny operational surface vs OpenSearch. Self-host on a single Fly machine + 1 GB persistent volume keeps cost ~$3–5/mo and avoids Meilisearch Cloud's per-tier pricing.

**Why a single machine (not HA=2).** Cost-tradeoff per user direction. Both `ritualworks-search` (stateless) and `ritualworks-meilisearch` (stateful) run as one VM each. Brief unavailability during Fly machine restart is accepted. Upgrade path documented: `flyctl scale count 2 --ha=true` on `ritualworks-search` is a one-command flip; Meilisearch HA needs a different topology (master/replicas via dump+restore or paid Cloud) and is out of scope until catalog size or query volume justifies it.

**Why search-svc is still its own service (not just hitting Meilisearch from the BFF).** Three reasons: (1) authn/authz lives at the search-svc boundary so we never expose Meilisearch's master key publicly; (2) the indexer needs to consume catalog events from RabbitMQ — that lifecycle belongs in a service, not the BFF; (3) the response shape gets translated to the Gemini-friendly envelope (`score`, `snippet`) in one place.

---

## 3. Contracts

### 3.1 HTTP — request/response

```
GET /search?q={text}&categoryId={guid?}&page={int=1}&pageSize={int=20}
   → 200 OK
   → 400 if q missing or pageSize > 100

Response:
{
  "query": "wireless headphones",
  "categoryId": null,
  "page": 1,
  "pageSize": 20,
  "totalHits": 137,
  "tookMs": 12,
  "hits": [
    {
      "productId": "…",
      "name": "…",
      "snippet": "…<em>wireless</em>…",     // ts_headline
      "categoryId": "…",
      "categoryName": "Audio",
      "unitPrice": 199.99,
      "isInStock": true,
      "score": 0.42                            // ts_rank_cd
    }
  ]
}
```

Snippet is generated server-side via `ts_headline` so the BFF doesn't have to highlight. `score` is exposed because Gemini (v2) will use it for re-ranking decisions.

**Health.** `GET /health` returns 200 when the DB ping succeeds AND the indexer consumer is registered. `/health/live` is process-up; `/health/ready` includes DB.

### 3.2 Inbound events

Search consumes from RabbitMQ (in-process MassTransit consumer, EF outbox per the existing pattern):

| Event                          | Source                | Action                                   |
| ------------------------------ | --------------------- | ---------------------------------------- |
| `ProductCacheInvalidatedEvent` | catalog (existing)    | fetch product → upsert SearchDocument; if `Reason=="deleted"`, hard-delete |
| `CategoryUpdatedEvent`         | catalog (**NEW**, §6) | re-denormalize `categoryName` for all products in that category |

**Note on category deletion.** The platform currently has no `DeleteCategoryCommand` in catalog — categories cannot be deleted in v1 of the platform. A `CategoryDeletedEvent` is therefore not part of this spec. When category deletion is implemented in a future phase, add the event + a `CategoryDeletedConsumer` then; the spec slot is reserved.

### 3.3 Outbound

None for v1. The service is read-only from the rest of the platform's perspective.

---

## 4. Data model — Meilisearch index

**Index name:** `products`. **Primary key:** `productId` (Meili rejects documents missing the PK; uuids are valid Meili IDs as long as they don't contain `-` — we strip dashes when sending: `productIdKey = productId.ToString("N")`, original `productId` kept as a separate field for round-tripping).

**Document shape:**

```jsonc
{
  "productIdKey":  "0a1b2c…",         // primary key, dash-free uuid
  "productId":     "0a1b-2c…",        // original uuid, returned to client
  "name":          "Wireless Headphones",
  "description":   "Bluetooth, noise-cancelling…",
  "categoryId":    "<guid>",
  "categoryName":  "Audio",
  "unitPrice":     199.99,
  "isInStock":     true,
  "isListed":      true,
  "sourceVersion": 42,                  // ProductCacheInvalidatedEvent.NewVersion, used for OOO suppression
  "indexedAt":     1715212800           // unix epoch seconds
}
```

**Index settings (set once at bootstrap, then again whenever B2 brief is rerun):**

```jsonc
{
  "searchableAttributes": ["name", "categoryName", "description"],
  "filterableAttributes": ["categoryId", "isListed", "isInStock"],
  "sortableAttributes":   ["unitPrice", "indexedAt"],
  "rankingRules": [
    "words", "typo", "proximity", "attribute", "sort", "exactness",
    "indexedAt:desc"      // freshness as final tiebreaker
  ],
  "typoTolerance": {
    "enabled": true,
    "minWordSizeForTypos": { "oneTypo": 4, "twoTypos": 8 }
  },
  "stopWords": [],         // English stopwords disabled — keep "the headphones" matchable
  "synonyms": {}           // populated later by ops if relevance feedback warrants
}
```

**Out-of-order event suppression.** Meilisearch has no native conditional upsert. We do it client-side in the indexer:

1. Fetch the current document via `index.getDocument(productIdKey)`.
2. If it exists and `existing.sourceVersion >= incoming.sourceVersion`, skip the write.
3. Otherwise call `index.addDocuments([…])` (Meili's add is upsert-by-PK).

This is a 1+1 round-trip per index event. At expected volume (catalog edits, not user traffic) this is fine. If indexer throughput becomes a hotspot, switch to a small Postgres `IndexerCheckpoint` table tracking max-applied version per product.

**No relational store in search-svc for v1.** The service has no DbContext. Removes a whole class of test-fixture work and migration overhead. (Section 8 still gives search-svc a Neon database in `bootstrap.sh` because the existing per-service-DB convention is too useful to break — it stays empty for v1, available for the checkpoint table later.)

---

## 5. Indexer pipeline

1. `ProductCacheInvalidatedConsumer` lives in `Search.Application/Consumers/`.
2. On `ProductCacheInvalidatedEvent`:
   - If `Reason == "deleted"` → call `index.deleteDocument(productIdKey)`. (Hard delete, not soft. Meilisearch has no `isListed=false` filter cost benefit and a hard-deleted doc is just gone from the index.)
   - Else → call catalog read API: `GET http://ritualworks-catalog.flycast:8080/api/products/{id}` returning the Product+Category projection.
   - Apply OOO version guard from §4, then `index.addDocuments([…])`.
3. On `CategoryUpdatedEvent` → query Meilisearch for all products in that category (`filter: categoryId = <guid>`), update `categoryName` on each, batch `addDocuments`. Meilisearch handles up to ~10k docs per batch comfortably; if a category has more, the indexer paginates.
4. Retries: MassTransit default exponential backoff. After max retries, message goes to error queue. **No DLQ-replay tooling for v1** — operator can re-trigger by republishing from catalog.
5. Catalog API is the single point of truth for product detail. If the BFF reads from search and search is stale by a few hundred ms, that's acceptable.

**Initial backfill.** First-time bring-up: call `POST /admin/reindex` (auth: identity-scoped admin role) which paginates `GET /api/products?skip=&take=` from catalog (catalog uses offset pagination, not cursor) — skipping in pages of 100 — collects each `productId`, then enriches each via `GET /api/products/{id}` (the per-product endpoint includes the denormalized `categoryName` that the list projection doesn't), and pushes batches of 1000 to Meilisearch. N+1 by design: backfill is rare and admin-triggered. Idempotent — safe to re-run.

---

## 6. Catalog-side changes (small but required)

One new event in `src/Contracts/Catalog/`:

```csharp
public sealed record CategoryUpdatedEvent : DomainEvent
{
    public required Guid CategoryId { get; init; }
    public required string Name { get; init; }
}
```

Catalog publishes this in the existing outbox transaction whenever `UpdateCategoryCommand` runs. **This is a mandatory dependency** of the search service — without it, category renames silently rot the index. (`DeleteCategoryCommand` does not exist in the platform yet; the corresponding event is deferred until it does.)

**Read endpoint.** Catalog already exposes `GET /api/products/{id}` via `Catalog.Api/Controllers/ProductsController.cs` returning the projection the search indexer needs (Name, Description, Category{Id,Name}, UnitPrice, IsInStock, IsListed). Confirmed during the planning research pass.

---

## 7. SLA targets

| Metric                                  | Target            | How measured |
| --------------------------------------- | ----------------- | ------------ |
| Search query p50 (internal flycast)     | < 25 ms           | trace span  |
| Search query p99                        | < 100 ms          | trace span  |
| Search query p99 (BFF-observed)         | < 250 ms          | BFF metric  |
| Index lag p99 (event → searchable)      | < 5 s             | end-to-end test + outbox poll-delay |
| Availability                            | 99.9% / 30d       | uptime check |
| Cold-start tolerance                    | none              | min_machines_running=1 |

---

## 8. Topology & deployment

**Two new Fly apps.** `ritualworks-search` (stateless) and `ritualworks-meilisearch` (stateful, with a 1 GB persistent volume mounted at `/meili_data`). Both single-machine, `shared-cpu-1x` 256 MB, region `iad`. Both internal (flycast only — no public IP).

`fly.search.toml` clones the catalog template:
- `min_machines_running = 1` (no auto-stop — kills tail latency)
- single-machine deploy (`--ha=false` in `deploy/fly/deploy.sh`, or just don't pass `--ha=true`)
- env: `Meilisearch__Url = http://ritualworks-meilisearch.flycast:7700`

`fly.meilisearch.toml` is new and is a stock Meilisearch container deploy:

```toml
app            = "ritualworks-meilisearch"
primary_region = "iad"

[build]
  image = "getmeili/meilisearch:v1.10"

[env]
  MEILI_NO_ANALYTICS = "true"
  MEILI_ENV          = "production"
  MEILI_DB_PATH      = "/meili_data/data.ms"
  MEILI_HTTP_ADDR    = "0.0.0.0:7700"

[mounts]
  source      = "meili_data"
  destination = "/meili_data"
  initial_size = "1gb"

[[services]]
  internal_port = 7700
  protocol      = "tcp"
  auto_stop_machines  = false       # stateful — keep the volume warm
  auto_start_machines = true
  min_machines_running = 1

[[vm]]
  memory   = "256mb"
  cpu_kind = "shared"
  cpus     = 1
```

The `MEILI_MASTER_KEY` is a Fly secret on both apps:
- on `ritualworks-meilisearch` → Meilisearch reads it on startup and locks down the API.
- on `ritualworks-search` → the SDK uses it as the bearer token.

**bootstrap.sh changes (B1 brief):**

```bash
INTERNAL_APPS=(
  ritualworks-identity ritualworks-catalog ritualworks-orders
  ritualworks-payments ritualworks-checkout
  ritualworks-search                         # new
  ritualworks-meilisearch                    # new
)
```

Plus a per-Meili-app block that creates the volume on first run and stages `MEILI_MASTER_KEY` (auto-generated like `JWT_SIGNING_KEY_PEM` is today, persisted to `.env.local`). The DB-string loop (`for app in "${INTERNAL_APPS[@]}"`) needs to skip `ritualworks-meilisearch` since it doesn't have a Postgres dependency — handled by an `if [[ "$app" != "ritualworks-meilisearch" ]]` guard.

**deploy.yml changes:** add `"search"` and `"meilisearch"` to the matrix-builder in the `plan` job. Both deploy on every push to main; no opt-in switch (search is core scope).

Cost: ~$3–5/mo (one extra shared-cpu-1x machine + 1 GB volume). The single-machine compromise is documented; upgrade path is `flyctl scale count 2 --ha=true` on `ritualworks-search` whenever HA becomes a priority. Meilisearch HA upgrade is non-trivial (master/replicas via dump+restore) and is a separate v3 effort.

---

## 9. Test plan

Three layers, each owned by a different agent so they parallelize cleanly.

### 9.1 Unit (`tests/Search.Unit/`)

Owner: agent A. No infra required, runs in seconds.

- `ProductIndexProjectorTests` — Product DTO → SearchDocument mapping; null/empty handling; trim rules; weight-class assignment.
- `SearchQueryParserTests` — query string sanitization (drop SQL-y chars, handle quoted phrases, max term count).
- `RankingTieBreakerTests` — equal `ts_rank_cd` falls back to `IsInStock DESC, IndexedAt DESC`.
- Coverage target: > 90% on Application + Domain (excluding generated EF code).

### 9.2 Integration (`tests/Search.Integration/`)

Owner: agent B. Mirrors the `PaymentsWebAppFactory` shape we just stabilized.

- `SearchWebAppFactory` — Testcontainers Postgres, env-vars-before-build, `EnsureSchemaAsync()`, MassTransit test harness with the indexer consumer registered.
- **Black-box query tests:**
  - `Search_returns_paged_hits_for_known_term`
  - `Search_filters_by_category`
  - `Search_returns_400_when_q_empty`
  - `Search_handles_typos_via_trgm` (insert "Headphones", query "headfones", expect a hit with score > threshold)
- **Indexer tests:**
  - `Publishing_ProductCacheInvalidated_upserts_SearchDocument` (with stubbed catalog HTTP client)
  - `Publishing_with_lower_SourceVersion_is_a_no_op` (out-of-order suppression)
  - `Publishing_CategoryUpdated_renames_category_for_all_products`
  - `Publishing_ProductCacheInvalidated_with_Reason_deleted_soft_deletes`
- **End-to-end lag test:** publish a synthetic event, poll `/search`, assert document searchable within 5s.

### 9.3 Performance (`tests/Search.Perf/`)

Owner: agent C, runs nightly only (not on every PR — too slow).

- BenchmarkDotNet harness against a Testcontainers Postgres seeded with 100k synthetic products.
- Scenarios: hot query (cached), cold query, category-filtered, typo'd. Asserts p99 stays under §7 targets. Failure marks the build red but does not block PRs.

### 9.4 Smoke (`tests/Smoke/`)

One assertion appended: `GET <bff>/search?q=test` returns 200 in < 1s post-deploy.

---

## 10. Observability

- **Traces:** OpenTelemetry already wired via `BuildingBlocks.Telemetry`. Spans on `Search.Query` and `Search.Index` with `q_length`, `category_filter`, `hit_count` attributes.
- **Metrics:** counter `search_queries_total{result=hit|miss|error}`, histogram `search_latency_ms`, gauge `search_documents_total`.
- **Logs:** structured, no payload bodies (q is fine; user identifiers are not).
- **Dashboard:** point an existing Grafana board at the new histograms — out of scope to build new dashboards in v1.

---

## 11. Failure modes & runbook stubs

| Failure                                     | Detection                  | Mitigation                                |
| ------------------------------------------- | -------------------------- | ----------------------------------------- |
| Catalog API down → indexer can't enrich     | 5xx burst on indexer span | MT retry, then dead-letter; manual replay |
| Index lag > 30s sustained                   | metric alert               | check RabbitMQ depth, scale consumer      |
| FTS query timeout > 1s                      | metric alert               | `EXPLAIN ANALYZE`, consider VACUUM        |
| Out-of-order events overwrite newer state   | impossible by §4 guard     | n/a                                       |
| Both Fly machines down                      | uptime check               | restart via flyctl; document recovery     |

---

## 12. Implementation plan (Gemini CLI agents)

Seven self-contained briefs in `docs/agent-briefs/search/`. Each brief is one Gemini CLI invocation. **Hard checkpoints between phases — the user reviews the done-report and only then launches the next phase.**

```
Phase 1: Scaffold + Fly plumbing  (1 agent, sequential — blocks everything)
  B1  src/Search skeleton (Domain/Application/Infrastructure/Api) + csproj graph
      + Dockerfile + fly.search.toml + fly.meilisearch.toml + bootstrap.sh
      entries + deploy.yml matrix + Search.Unit / Search.Integration project
      shells.
      → CHECKPOINT: dotnet build clean, dotnet test (empty test projects) green,
        flyctl config validate fly.search.toml fly.meilisearch.toml passes.

Phase 2: Three independent tracks — fire all three Gemini CLI agents in parallel
  B2  Meilisearch index settings + admin/reindex bootstrap (no consumer yet, just
      a typed MeilisearchClient wrapper + an idempotent "ensure index settings"
      bootstrap that runs on app start).
  B3  Catalog: add CategoryUpdatedEvent + CategoryDeletedEvent contract records,
      publish from UpdateCategoryCommand and DeleteCategoryCommand handlers,
      one new integration test per event verifying the publish.
  B4  Catalog HTTP client in search-svc Infrastructure: typed Refit interface
      + Polly retry/timeout policy + integration test against a WireMock stub.
      → CHECKPOINT: each brief's tests green in isolation; B3 events visible
        on the RabbitMQ harness during catalog tests.

Phase 3: Pipeline — two parallel agents (both depend on B2+B3+B4)
  B5  ProductCacheInvalidatedConsumer + CategoryUpdatedConsumer +
      CategoryDeletedConsumer + projector (Product DTO → Meili document) +
      OOO version guard + integration tests against Testcontainers Meili.
  B6  GET /search endpoint + query parser + Meili.search() invocation +
      Gemini-shaped response envelope + integration tests covering every
      assertion in §9.2.
      → CHECKPOINT: every test in §9.2 green; manual curl against local
        docker-compose returns sensible hits.

Phase 4: Wire + ship (1 agent, sequential)
  B7  BFF route /search → search-svc flycast; smoke test entry; staging deploy
      via the existing GitHub Actions Deploy workflow; user-visible curl
      against the deployed BFF endpoint returns a hit.
      → CHECKPOINT: production-grade smoke passes; spec is shipped.

Phase 5: Defer until v1 is in user's hands
  (Perf nightly job, ops runbook, backfill endpoint hardening — written when
   the v1 surface area is settled.)
```

**Anti-stuck rules baked into every brief.** Repeated for emphasis since Gemini CLI agents lose context faster than humans:

1. Read the **Inputs** section before writing any code. Don't grep the codebase blindly.
2. Stay inside the **Deliverable** scope. If you see a tempting refactor, **don't do it** — note it in the done-report under "out-of-scope observations" instead.
3. **Acceptance** commands are non-negotiable. If they don't pass, you're not done. If they can't pass for a reason outside your control, write a `blocker:` line per the protocol doc and stop.
4. Hard time budget per brief: ~30 min of agent time. If you're stuck past 30 min, stop and emit a blocker — don't keep retrying.
5. **No cross-brief edits.** B5 must not modify the Catalog HTTP client; that's B4's territory. If you discover B4 missed something, file a blocker, don't patch.
6. Done-report format is fixed (see protocol doc). Stick to it; the reviewer reads dozens of these and cannot afford prose.

---

## 13. Sign-off (2026-05-08)

| Question                                  | Decision                                                |
| ----------------------------------------- | ------------------------------------------------------- |
| Search engine                             | **Meilisearch** (self-hosted on Fly with 1 GB volume)   |
| HA topology                               | **Single machine** for both apps; HA upgrade documented |
| Catalog `Category*Event` records          | **Yes**, land in B3                                     |
| Gemini API calls in v1                    | **No** — defer to v2 (`assistant-svc` brief)            |
| Implementer                               | Gemini CLI agents, brief-by-brief                       |
| Reviewer                                  | Claude / user, between phases                           |
