# Search service — deployment runbook

Operational notes for the `haworks-search` + `haworks-meilisearch` Fly apps. The architectural reasoning lives in `docs/agent-briefs/search-service-spec.md`; this file is the *what to type* companion.

---

## What's deployed

| Fly app                   | Image / source                                | Purpose                                       | Public? |
| ------------------------- | --------------------------------------------- | --------------------------------------------- | ------- |
| `haworks-search`      | `src/Search/Search.Api/Dockerfile`            | ASP.NET stateless HTTP API, Meilisearch SDK   | No (flycast / `.internal`) |
| `haworks-meilisearch` | `getmeili/meilisearch:v1.10` (vendor image)   | Stateful index, 1 GB persistent volume        | No (flycast / `.internal`) |
| `haworks-bffweb`      | (existing)                                    | Owns `/api/search` route, proxies to search-svc | Yes  |

`search-svc` consumes catalog events (`ProductCacheInvalidatedEvent`, `CategoryUpdatedEvent`) from the existing RabbitMQ broker — same MassTransit + outbox stack as every other service.

---

## First-time bring-up (one-shot)

The platform's bootstrap script provisions Fly apps, volumes, and stages secrets idempotently. From a developer machine with `flyctl auth login` already done and `deploy/fly/.env.local` populated:

```bash
deploy/fly/bootstrap.sh
```

What this does for the search stack specifically:

1. **Auto-generates `MEILI_MASTER_KEY`** (32 bytes urandom, base64) on first run, persists to `.env.local`.
2. **Creates `haworks-search`** and **`haworks-meilisearch`** apps if missing.
3. **Stages connection-string + master-key secrets** on both apps:
   - `haworks-search` ← `Meilisearch__MasterKey`, `ConnectionStrings__rabbitmq`, `ConnectionStrings__redis`, `ConnectionStrings__search` (Postgres, currently unused — reserved for the future `IndexerCheckpoint` table per spec §4)
   - `haworks-meilisearch` ← `MEILI_MASTER_KEY`. Skips Postgres (no DB dependency).
4. **Creates the `meili_data` volume** on `haworks-meilisearch` (1 GB, region from `$REGION`, default `iad`) if missing.

The script is safe to re-run after editing `.env.local` — secrets restage; existing apps and volumes are skipped.

---

## Routine deploy

`.github/workflows/deploy.yml` deploys on every push to `main` after CI passes. The `plan` job's matrix already includes both `search` and `meilisearch`. No per-deploy human steps once the first-time bring-up has run.

To watch a deploy:

```bash
gh run watch $(gh run list --workflow Deploy --limit 1 --json databaseId -q '.[0].databaseId')
```

---

## First-time Meilisearch index settings

The first cold start of `haworks-search` runs `ISearchIndex.EnsureSettingsAsync()` from `Program.cs`, which:

- Creates the `products` index with `productIdKey` as primary key (idempotent — existing indexes are skipped).
- Applies the `searchableAttributes` / `filterableAttributes` / `sortableAttributes` / `rankingRules` blocks from spec §4.

If `haworks-meilisearch` is unreachable when search-svc boots (e.g. they came up out of order), settings are logged-and-deferred — the next request will trigger another EnsureSettings via the consumer DI scope. **No manual action required.**

---

## Backfill (initial product population)

There is no backfill endpoint in v1. The search index will populate naturally as catalog publishes `ProductCacheInvalidatedEvent` for each existing product on the next bulk re-cache. If immediate population is needed, deferred to a future brief.

For now, dev verification: after deploy, exercise an indexed product by editing it in catalog (which fires the event) and then calling `/api/search?q=<known-term>` against the BFF.

---

## Smoke test

After Deploy goes green:

```bash
SMOKE_TARGET_URL=https://haworks-bffweb.fly.dev \
    dotnet test tests/Smoke -c Release \
    --filter "FullyQualifiedName~SearchSmokeTests"
```

The smoke test asserts the BFF route is reachable and the response envelope (per spec §3.1) is intact. It does **not** require a populated index — the `hits` array may be empty.

---

## Cost & topology trade-offs (per spec §8)

- **Single machine each.** `haworks-search` and `haworks-meilisearch` both run as a single `shared-cpu-1x` 256 MB VM. Brief unavailability during Fly machine restart is accepted.
- **Total monthly cost:** ~$3–5 (one extra VM + 1 GB volume, both well under Fly's free tier line-items at this size).
- **HA upgrade path:**
  - `haworks-search` (stateless) → `flyctl scale count 2 --ha=true -a haworks-search`. One-command flip.
  - `haworks-meilisearch` (stateful) → non-trivial. Master/replica via `meilisearch dump` + `--import-dump`, or move to Meilisearch Cloud. Document and decide when query volume justifies it.

---

## Common operations

```bash
# Inspect the volume (size, attached machine, snapshots)
flyctl volumes list -a haworks-meilisearch

# Tail Meilisearch logs (a tail of "task processed" lines confirms indexing is live)
flyctl logs -a haworks-meilisearch

# Tail search-svc logs
flyctl logs -a haworks-search

# Curl Meilisearch from another Fly machine (won't work from your laptop — flycast is private)
flyctl ssh console -a haworks-bffweb -C 'curl -s http://haworks-meilisearch.flycast:7700/health'

# Rotate MEILI_MASTER_KEY (must update both apps in lockstep — search-svc calls Meili with this key as bearer token)
new_key="$(head -c 32 /dev/urandom | base64 | tr -d '\n')"
flyctl secrets set MEILI_MASTER_KEY="$new_key" -a haworks-meilisearch
flyctl secrets set Meilisearch__MasterKey="$new_key" -a haworks-search
# Both apps will roll automatically. Then update .env.local so the next bootstrap.sh run doesn't regenerate.
```

---

## Failure modes — diagnostic order

Per spec §11. Numbered for triage:

1. **`/api/search` returns 502/503.** Check `haworks-search` is up: `flyctl status -a haworks-search`. If stopped, restart: `flyctl machine restart -a haworks-search`.
2. **search-svc up but `/search` returns 5xx.** Check Meilisearch: `flyctl logs -a haworks-meilisearch`. Most often the master-key got out of sync — re-run the rotation block above.
3. **Index lag (event published but not searchable for >30s).** RabbitMQ depth on the `product-cache-invalidated` queue: `flyctl logs -a haworks-search | grep PaymentWebhook` for consume activity.
4. **Catalog API down → indexer can't enrich.** Visible as repeated 5xx in search-svc logs from `CatalogProductsApiClient`. The Polly policy retries; persistent failure means catalog itself is sick — escalate there.
5. **Meilisearch volume full.** `flyctl volumes list -a haworks-meilisearch` shows used size. At 1 GB the index supports ~100k docs comfortably; resize via `flyctl volumes extend <vol_id> --size 5 -a haworks-meilisearch` (requires machine restart).
