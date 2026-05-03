# CHECKPOINT — where we are, what's next

> **Living doc.** Updated after each phase milestone. Read this first if returning to the project after time away.

**Last updated:** 2026-05-03 (Phase 1 complete)
**Current phase:** Phase 1 (identity-svc) — **DONE**, ready for Phase 2 (catalog-svc)

---

## TL;DR

Strict monorepo at `/Users/chidionyema/Documents/code/ritualworks-platform/`. Architecture, build plan, and ADRs live in [`docs/microservices-migration/`](./microservices-migration/README.md).

`dotnet run --project deploy/aspire` brings up the full stack including identity-svc. `/health` returns 200. **Register / login / JWKS round-trip works end-to-end.** JWTs are signed RS256 with RSA-2048 keypair sourced from Vault; matching public key is published at `/.well-known/jwks.json`. 6/6 integration tests pass; 1/1 Pact contract test publishes a `UserProfileChangedEvent` pact for future consumers.

---

## Commits to date (newest first)

```
<this commit>  identity-svc: Pact contract for UserProfileChangedEvent + CHECKPOINT update
<integration>  identity-svc: integration tests — register / login / JWKS, 6/6 green
<rs256>        identity-svc: switch JWT to RS256 + JWKS endpoint (ADR-0005)
0f56430        identity-svc: Vault wiring + role seeding -> register/login green
2204fee        identity-svc: EF migrations + auto-migrate, boundary tests, runbooks
0f0ecaf        identity-svc: add launchSettings so Aspire injects ASPNETCORE_URLS
4caa73d        identity-svc: DI wired, runs end-to-end on http://*/health → 200
37d32fa        Phase 1 (in progress): identity-svc compiles end-to-end
ee70fb8        Phase 0: Foundation — monorepo, BuildingBlocks, Contracts, Aspire, docs
```

(Note: 3 of the recent commits share an identical message because `/tmp/commit-msg.txt`
was reused across multiple `git commit -F` invocations. The diffs are distinct;
just the messages collide. Future commits use unique messages.)

---

## What works end-to-end RIGHT NOW

| Surface | State |
|---|---|
| `dotnet build RitualworksPlatform.sln` | 0 warnings, 0 errors |
| `dotnet test tests/Identity.Architecture` | 3/3 boundary tests pass |
| `dotnet test tests/Identity.Integration` (with TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock) | 6/6 register/login/JWKS tests pass in ~19s |
| `dotnet test tests/Identity.Contract` | 1/1 Pact contract test passes; pact JSON written to `tests/pacts/` |
| `dotnet run --project deploy/aspire` | Brings up 10 infra containers + identity-svc |
| Aspire dashboard at https://localhost:17000 | All resources Ready |
| `vault-init` one-shot | Exits 0; writes 7 per-service AppRole creds to `deploy/aspire/vault-creds/<svc>/{role_id,secret_id}` |
| `vault-seed` one-shot | Exits 0; KV paths populated per `infra/vault/secrets/kv-dev-values.json` |
| `curl https://localhost:7101/health` | HTTP 200 |
| `curl https://localhost:7101/.well-known/jwks.json` | RSA JWK with `kty=RSA, alg=RS256, use=sig, kid, n, e` |
| `POST /api/Authentication/register` | HTTP 201 with RS256 JWT (header `alg=RS256, kid=<matches JWKS>`) |
| `POST /api/Authentication/login` | HTTP 200 with JWT + refresh token |

---

## What does NOT work yet (Phase 2+ scope)

| Item | Tracked |
|---|---|
| catalog-svc, orders-svc, payments-svc, content-svc, checkout-orchestrator-svc, bff-web | Phase 2–7 |
| Cross-service Pact provider verification (only consumer pact published; no provider replays it yet) | When second service is added |
| Helm charts + ArgoCD apps for kind cluster | Phase 8 (or earlier as we ship services) |
| `make demo-saga-failure` (the headline chaos demo) | Phase 5 |

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

## Phase 1 — ALL TASKS COMPLETE

| # | Task | Status | Verification |
|---|---|---|---|
| 19 | CHECKPOINT.md | ✅ | this doc |
| 20 | Runbook: Serilog silent-swallow | ✅ | `docs/runbooks/serilog-silent-swallow.md` |
| 21 | Runbook: Aspire launchSettings required | ✅ | `docs/runbooks/aspire-launchsettings-required.md` |
| 22 | Identity boundary architecture test | ✅ | `tests/Identity.Architecture` 3/3 pass |
| 23 | EF migrations + auto-migrate | ✅ | 10 tables in `identity` schema after AppHost boot |
| 24 | Wire VaultConfigBootstrap into Identity.Api | ✅ | `Jwt:Key` flows from `secret/identity/jwt` |
| 25 | Switch JWT to RS256/JWKS | ✅ | JWT header `alg=RS256, kid=...`; JWKS endpoint serves matching public key |
| 26 | Integration tests | ✅ | `tests/Identity.Integration` 6/6 pass (register, login, JWKS, JWT-kid match, etc.) |
| 27 | Pact contract tests | ✅ | `pacts/ConsumerOfIdentity-identity-svc.json` generated for `UserProfileChangedEvent` |

---

## After Phase 1, the build plan continues

Per [`microservices-migration/03-build-plan.md`](./microservices-migration/03-build-plan.md):

| Phase | Service | Status |
|---|---|---|
| 0 | Foundation | ✅ Done |
| 1 | identity-svc | ✅ Done |
| 2 | catalog-svc | 🟡 Next |
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
