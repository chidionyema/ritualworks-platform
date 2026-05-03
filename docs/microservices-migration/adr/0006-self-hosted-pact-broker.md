# ADR-0006: Self-Hosted Pact Broker

**Status:** Accepted
**Date:** 2026-05-02
**Deciders:** chidionyema

## Context

Contract testing is the load-bearing safety net for the migration. The current `tests/haworks.Tests.Contract/Catalog/CatalogToPaymentsContractTests.cs` writes pact JSON to local files — sufficient for one event in a monolith but not for a polyrepo with 13+ events across 7 services.

We need a **broker** that:
- Stores pacts versioned by consumer + producer.
- Serves the `can-i-deploy` query that gates PRs.
- Triggers webhooks when contracts change so downstream services re-verify.
- Has a UI for the contract matrix view.

Two paths: hosted SaaS (Pactflow) or self-hosted (`pactfoundation/pact-broker` Docker image).

## Decision

**Self-host the Pact broker via Docker Compose locally + Helm in kind.**

- **Local dev:** `infra/pact-broker/docker-compose.yml` runs `pactfoundation/pact-broker:latest` + a Postgres sidecar. Wired into Aspire AppHost via `AddContainer("pact-broker", ...)`.
- **kind:** `deploy/helm/pact-broker/` ships a Helm chart, ArgoCD-managed.
- **CI:** publishes pacts via `pact-broker publish ./pacts --consumer-app-version $SHA --branch $BRANCH --broker-base-url $PACT_BROKER_URL`.
- **Webhook on `contract_content_changed`** triggers downstream consumer-pipeline runs.

Postgres backing store is part of the broker deployment (no shared DB cluster).

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Self-hosted (chosen)** | Free. Demonstrates infra/DevOps depth (the broker, its DB, its backups, are all in the repo). Full control over webhook configuration. Runnable locally without external dependencies. | One more service to operate. **Mitigation:** docker-compose; restore in <2 min from KV backup. | **Chosen.** Aligns with portfolio "show the full stack" optimization. |
| Pactflow SaaS | Zero ops. Polished UI. Built-in webhooks. | Costs money ($35-150/mo). External dependency (network failure breaks CI). Vendor lock-in for the broker URL. Recruiter sees "uses a SaaS" not "built one." | Rejected for portfolio. **Would adopt for a real enterprise team.** |
| File-based pacts in git | Free, no broker. | No `can-i-deploy`. No matrix view. Hard to gate PRs. Doesn't scale past 2-3 services. | Rejected. Fundamental tooling gap. |
| Stub broker (just a HTTP endpoint that always returns true) | Cheapest "we have contract tests" claim. | Not a contract testing system. Defeats the point. | Rejected. |

## Consequences

### Positive
- Pact broker UI matrix view (showing all 13 events green across 6 producer/consumer pairs) is itself a portfolio screenshot for the README hero.
- `can-i-deploy` runs in CI without external dependencies — works on a flight, behind a firewall, in any environment.
- Demonstrates full-stack DevOps capability (running + operating + backing up a stateful service).
- Same broker URL pattern in dev, CI, prod-shape demo: `http://pact-broker:9292` in dev/kind, `https://pact.<domain>` if hosting publicly.

### Negative
- One more service to keep healthy. **Mitigation:** small operational surface; backups are just Postgres dumps to an S3 bucket nightly.
- No vendor SLA. **Mitigation:** broker is stateless plus Postgres; restore is `docker-compose up` + `pg_restore`.
- No fancy UI features (like Pactflow's "contract drift" insights). **Acceptable.**

### Neutral
- Pact broker docs assume hosted use; some advanced features (e.g., per-environment tagging strategy) require reading the OSS docs more carefully. One-time learning cost.

## Notes

Backup strategy:
- Nightly `pg_dump` of the broker's Postgres → encrypted artifact in S3 (or local volume in dev).
- 30-day retention.
- Restore script in `infra/pact-broker/restore.sh` documented in `docs/runbooks/pact-broker-restore.md`.

Auth:
- Local dev: no auth (broker behind localhost firewall).
- Public demo (if deployed): basic auth via `PACT_BROKER_BASIC_AUTH_USERNAME` / `PACT_BROKER_BASIC_AUTH_PASSWORD` env vars from Vault.

Reference: [02-platform.md § Self-Hosted Pact Broker](../02-platform.md#self-hosted-pact-broker)
