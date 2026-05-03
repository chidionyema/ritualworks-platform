# CHECKPOINT — where we are, what's next

> **Living doc.** Updated after each phase milestone. Read this first if returning to the project after time away.

**Last updated:** 2026-05-03
**Current phase:** Phase 1 (identity-svc) — in progress

---

## TL;DR

Strict monorepo at `/Users/chidionyema/Documents/code/ritualworks-platform/`. Architecture, build plan, and ADRs live in [`docs/microservices-migration/`](./microservices-migration/README.md).

`dotnet run --project deploy/aspire` brings up the full stack including identity-svc. `/health` returns 200. Auth flows still 500 until EF migrations land (Phase 1 task #23).

---

## Commits to date (newest first)

```
0f0ecaf identity-svc: add launchSettings so Aspire injects ASPNETCORE_URLS
4caa73d identity-svc: DI wired, runs end-to-end on http://*/health → 200
37d32fa Phase 1 (in progress): identity-svc compiles end-to-end
ee70fb8 Phase 0: Foundation — monorepo, BuildingBlocks, Contracts, Aspire, docs
```

---

## What works end-to-end RIGHT NOW

| Surface | State |
|---|---|
| `dotnet build RitualworksPlatform.sln` | 0 warnings, 0 errors |
| `dotnet run --project deploy/aspire` | Brings up 10 infra containers + identity-svc |
| Aspire dashboard at https://localhost:17000 | All resources Ready |
| `vault-init` one-shot | Exits 0; writes 7 per-service AppRole creds to `deploy/aspire/vault-creds/<svc>/{role_id,secret_id}` |
| `vault-seed` one-shot | Exits 0; KV paths populated per `infra/vault/secrets/kv-dev-values.json` |
| `curl https://localhost:7101/health` (identity-svc) | HTTP 200 |
| `curl http://localhost:5101/health` (identity-svc) | HTTP 307 → HTTPS redirect |

---

## What does NOT work yet

| Endpoint | Why | Tracked |
|---|---|---|
| `POST /auth/register` | EF migrations not applied; tables don't exist in `identity` DB | Task #23 |
| `POST /auth/login` | Same | Task #23 |
| Any JWT issuance | `Jwt:Key` is a placeholder in appsettings, not from Vault | Task #24 |
| RS256/JWKS validation | Currently HS256 (per ADR-0005, RS256 is target) | Task #25 |
| Cross-service contract verification | No Pact tests yet | Task #27 |

---

## Pinned facts (don't relearn these)

### Service ports (per `launchSettings.json`)
- **identity-svc**: HTTP 5101, HTTPS 7101
- (Other services: declare in their own launchSettings as added.)

### Aspire dashboard
- HTTP 15000, HTTPS 17000 (per `deploy/aspire/Properties/launchSettings.json`)
- DCP API: 22000

### Vault
- Dev address: `http://vault:8200` inside containers, `http://localhost:<random>` from host
- Bootstrap token (DEV ONLY): `dev-root-token`
- Per-service AppRoles: `haworks-identity`, `haworks-catalog`, `haworks-orders`, `haworks-payments`, `haworks-content`, `haworks-checkout-orchestrator`, `haworks-bff-web`
- KV paths: `secret/identity/{jwt,oauth/google,oauth/microsoft,oauth/facebook}`, `secret/payments/{stripe,paypal}`, `secret/bff-web/hub`

### Postgres
- Single Aspire-managed cluster, namespaced volume `ritualworks-platform-postgres-data`
- Per-service databases: `identity`, `catalog`, `orders`, `payments`, `content`, `checkout`
- Per-DB owner roles: `<db>_owner` (NOLOGIN; Vault dynamic users join these)

### Pact broker
- Self-hosted (`pactfoundation/pact-broker:latest`)
- Aspire-managed, port: random (set by Aspire)
- Postgres backing: `pact-db` Aspire resource

---

## Two debugging gotchas — see runbooks

1. **Serilog silent-swallow** — see [`runbooks/serilog-silent-swallow.md`](./runbooks/serilog-silent-swallow.md). When `appsettings.Serilog` shape is wrong, ALL log output disappears (Kestrel's "Now listening" included). Looks identical to a hang.

2. **Aspire + missing launchSettings** — see [`runbooks/aspire-launchsettings-required.md`](./runbooks/aspire-launchsettings-required.md). Without `Properties/launchSettings.json`, Aspire's `AddProject<T>()` skips ASPNETCORE_URLS injection. Kestrel falls back to default 5000 → collides with macOS ControlCenter → silent hang.

---

## Phase 1 remaining work (in execution order)

| # | Task | Status |
|---|---|---|
| 19 | Write CHECKPOINT.md (this doc) | **in_progress** |
| 20 | Runbook: Serilog silent-swallow | pending |
| 21 | Runbook: Aspire launchSettings required | pending |
| 22 | Identity boundary architecture test | pending |
| 23 | **EF migrations + auto-migrate** | pending |
| 24 | Wire VaultConfigBootstrap into Identity.Api | pending |
| 25 | Switch JWT to RS256/JWKS | pending |
| 26 | Integration tests (register/login/refresh) | pending |
| 27 | Pact contract tests | pending |

---

## After Phase 1, the build plan continues

Per [`microservices-migration/03-build-plan.md`](./microservices-migration/03-build-plan.md):

| Phase | Service | Status |
|---|---|---|
| 0 | Foundation | ✅ Done |
| 1 | identity-svc | 🟡 In progress |
| 2 | catalog-svc | ⏳ |
| 3 | payments-svc | ⏳ |
| 4 | orders-svc | ⏳ |
| 5 | checkout-orchestrator-svc (the saga — crown jewel) | ⏳ |
| 6 | content-svc | ⏳ |
| 7 | bff-web | ⏳ |
| 8 | Polish + case study + demo videos | ⏳ |

---

## How to pick up where we left off

1. `cd /Users/chidionyema/Documents/code/ritualworks-platform`
2. Read this file (you're doing it)
3. Skim `git log --oneline` for recent commits
4. Check `TaskList` for outstanding items (or read the table above)
5. `dotnet build RitualworksPlatform.sln` — should be 0 warnings, 0 errors
6. `dotnet run --project deploy/aspire` — full stack should come up; verify dashboard URL in console
7. Pick the next pending task and grind
