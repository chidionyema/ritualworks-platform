# Vault Production Readiness Backlog

> Last reviewed: 2026-05-18 | Staff Engineer: 7.5/10 | QA Engineer: 6.5/10

## Current Scores

| Reviewer | Score | Verdict |
|----------|-------|---------|
| Staff Engineer | 7.5/10 | Conditional GO |
| Senior QA Engineer | 6.5/10 | Conditional Hold |

## What's needed for 10/10

### Staff Engineer Findings (7.5 -> 10)

#### MUST FIX (blocks 10/10)

| ID | Severity | Finding | File | Fix | Effort |
|----|----------|---------|------|-----|--------|
| N-01 | HIGH | HttpClient leak on VaultClient rebuild. Each token re-auth (~1h) creates a new HttpClient that is never disposed. VaultSharp's VaultClient doesn't implement IDisposable. | `VaultClientFactory.cs:85` | Make VaultClientHandle implement IDisposable, store the HttpClient, dispose the old handle in VaultService.BuildClientAsync before creating new one. | S |
| N-03 | MEDIUM | Vault Agent db-creds.ctmpl uses literal SERVICE_ROLE placeholder requiring manual substitution per service. Error-prone for K8s operators. | `infra/vault-agent/templates/db-creds.ctmpl:4` | Use Consul Template `env` function: `{{ env "VAULT_DB_ROLE" }}`. Set VAULT_DB_ROLE in K8s pod spec or agent config per service. | S |
| ~~N-06~~ | ~~MEDIUM~~ | ~~flyio/postgres-flex init scripts only run on empty data directory (first boot). Adding new service roles to init.sql won't take effect on existing deployments.~~ | ~~`deploy/vault-pg/Dockerfile:2`~~ | ~~Document migration path: new roles on existing deployments require manual `psql` or a separate migration script. Add to ops runbook.~~ | ~~S~~ |

#### SHOULD FIX (improves score)

| ID | Severity | Finding | File | Fix | Effort |
|----|----------|---------|------|-----|--------|
| ~~N-02~~ | ~~LOW~~ | ~~init.sql ALTER DEFAULT PRIVILEGES only affects future tables. Missing GRANT ALL ON existing tables.~~ | ~~`deploy/vault-pg/init.sql:51`~~ | ~~Add `GRANT ALL ON ALL TABLES IN SCHEMA public TO ...;` after ALTER DEFAULT lines.~~ | ~~S~~ |
| ~~N-04~~ | ~~LOW~~ | ~~VaultClientFactory SemaphoreSlim (_unwrapGate) never disposed. Singleton lifetime so only leaks on shutdown.~~ | ~~`VaultClientFactory.cs:34`~~ | ~~Implement IDisposable and dispose _unwrapGate.~~ | ~~S~~ |
| N-05 | LOW | Empty agent credential file (0 bytes during atomic rename) produces cryptic JsonException instead of clear message. | `VaultServiceCollectionExtensions.cs:192` | Add `if (string.IsNullOrWhiteSpace(json)) return error("file is empty")` before JsonDocument.Parse. | S |

---

### QA Engineer Findings (6.5 -> 10)

#### P0 — Blocks sign-off

| ID | Test | What it catches | Effort |
|----|------|-----------------|--------|
| QA-01 | `StartCredentialRenewalAsync` unit test: verify loop calls RefreshCredentials for cached roles, sleeps until earliest expiry, handles errors without dying | Silent credential expiry in production — the background loop is the heart of rotation and has zero test coverage | M |
| QA-02 | `PeriodicPasswordProvider` integration test: build NpgsqlDataSource via AddVaultNpgsqlDataSource, trigger callback, verify password rotated | Regression in DI wiring or UsePeriodicPasswordProvider lambda would cause all DB connections to fail on next rotation | M |
| QA-03 | Circuit breaker unit test: trigger enough failures to open breaker, verify requests rejected, wait for reset, verify success | Misconfigured threshold (break after 1 vs 5 failures) goes undetected | M |

#### P1 — High risk gaps

| ID | Test | What it catches | Effort |
|----|------|-----------------|--------|
| QA-04 | VaultConfigBootstrap retry filter: 403 NOT retried, 503 IS retried | Non-transient errors cause 62s startup delay instead of fail-fast | S |
| QA-05 | GetDatabaseConnectionStringAsync: verify NpgsqlConnectionStringBuilder output, SSL mode parsing | Connection string formatting bugs | S |
| QA-06 | VaultCredentialProvider HttpRequestException + TaskCanceledException stale fallback (currently only 503 tested) | Network errors and timeouts cause crashes instead of graceful degradation | S |
| QA-07 | AgentFile sb.Username update: verify NpgsqlConnectionStringBuilder username changes when agent file provides one | Username-update path in PeriodicPasswordProvider is untested | S |
| QA-08 | VaultAppRoleAuthenticator lease_duration=0 maps to 86400 | Re-auth storms with root/unlimited tokens | S |
| QA-09 | ValidateRoleName throws on null/empty | Empty strings passed to Vault API | S |
| QA-10 | Wrapped secret_id expired TTL test | Expired wrapping tokens give actionable error | S |
| QA-11 | Contract test: services in src/ missing from services.json (reverse direction) | Service deployed without Vault role provisioned | S |

#### P2 — Nice to have

| ID | Test | What it catches | Effort |
|----|------|-----------------|--------|
| QA-12 | Structured log verification (specific log messages at rotation, failure, circuit-break events) | Observability regression | S |
| QA-13 | Metrics integration test: verify VaultMetrics.AuthSuccess.Add(1) called during LoginAsync | Silent metrics regression | S |
| QA-14 | VaultConfigBootstrap.WaitForFileAsync 60s timeout test | Startup hangs in path-based mode | S |
| QA-15 | Integration test Docker skip via [Trait("RequiresDocker", "true")] | Local dev fails confusingly without Docker | S |
| QA-16 | Partial file write / truncated JSON in AgentFile reader | Corrupt credentials during rotation window | S |
| QA-17 | SharedTestVault secret_id_ttl consistency with contract tests (currently "0" in test infra vs "720h" enforced by contract) | Cargo-culting test config into prod | S |

---

## Scoring Roadmap

| Score | What's needed |
|-------|---------------|
| Current (7.0 avg) | Ship as-is with monitoring |
| 8.0 | Fix N-01 (HttpClient leak) + QA-01 (renewal loop test) + QA-02 (PeriodicPasswordProvider test) |
| 9.0 | Above + N-03 (template env vars) + N-06 (ops runbook) + QA-03 (circuit breaker test) + all P1 tests |
| 10.0 | Above + all P2 tests + N-02, N-04, N-05 LOW fixes |

## Definition of Done for each item

- Code change compiles with 0 warnings
- Tests pass locally (`dotnet test --filter Vault`)
- Architecture guards pass (`scripts/check-architecture.sh`)
- PR reviewed by at least one other engineer
- Updated in this backlog (move to "Done" section below)

## Done

| ID | Date | PR |
|----|------|----|
| F-01 through F-12 | 2026-05-18 | #221 |
| Phase 1-6 | 2026-05-18 | #221 |
| N-02 | 2026-05-18 | — |
| N-04 | 2026-05-18 | — |
| N-06 | 2026-05-18 | — |
