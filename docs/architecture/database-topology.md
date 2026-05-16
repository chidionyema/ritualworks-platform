# Database topology

**As of:** 2026-05-10. Updated when the topology changes (Fly → CNPG migration is the next planned change, per `agent-briefs/k8s-platform-spec.md`).

This is the source-of-truth document for "where does each service's data actually live in production?" — the answer most readers need before they can reason about CDC, backups, migrations, capacity, or DR.

## TL;DR

**One Postgres instance**, many logical databases, one logical DB per service. Vault issues dynamic per-service credentials. The Postgres instance is named `haworks-vault-pg` for historical reasons (Vault's first use predated the migration of every other service onto the same instance).

```
haworks-vault-pg.internal:5432   (Fly Postgres, single instance, iad region)
├── postgres        ← Vault's own metadata (KV, AppRole, audit log, etc.)
├── identity        ← Identity service
├── catalog         ← Catalog service
├── orders          ← Orders service
├── payments        ← Payments service
├── content         ← Content service
├── checkout        ← CheckoutOrchestrator service
├── audit           ← Audit service
└── notifications   ← Notifications service
```

## Production specifics

| Property | Value |
|---|---|
| Provider | Fly.io Postgres (managed, single primary) |
| App name | `haworks-vault-pg` |
| Internal hostname | `haworks-vault-pg.internal:5432` |
| Region | `iad` |
| Postgres version | (inherits from `fly-postgres-flex` base — typically PG 16) |
| Replication | None today — single primary. (HA replicas live in the future CNPG plan, not Fly.) |
| Backups | Fly's snapshot mechanism (daily, 7-day retention by default) |
| TLS | `sslmode=disable` on internal flycast (the network is the trust boundary on Fly) |
| Connection pool | PgBouncer in front, transaction-mode by default |

## How services connect — Vault dynamic credentials

Services NEVER hold static DB credentials. The connection string in each service's environment is built from a Vault-issued ephemeral user that's revoked after a TTL (default 1 hour, renewable).

### The chain

```
1. Service boots → reads Vault__Address + Vault__RoleId + Vault__SecretId from Fly secrets
2. Service authenticates to Vault → AppRole login → gets a Vault token (TTL ~24h)
3. Service requests a DB credential → POST /v1/database/creds/haworks-<service>
4. Vault generates an ephemeral Postgres user → CREATE ROLE v-<random> ... IN ROLE <service>_owner
5. Service uses (v-<random>, password) to connect → ConnectionStrings__<service> assembled at boot
6. TTL expires → service requests a fresh cred → repeat
```

### Per-service Vault role mapping

Documented in `infra/vault/services.json`. Each row binds a service to:
- A Vault AppRole (`haworks-<service>-app`) for the service-to-Vault auth
- A DB role (`haworks-<service>`) that issues ephemeral Postgres users in the `<service>_owner` group

The `_owner` group has `GRANT ALL ON DATABASE <service>` (per `deploy/aspire/init-postgres.sql`). Vault-issued ephemerals join the group and inherit the privileges.

### Why this matters for CDC, observability, runbooks

CDC needs an additional Vault role with `REPLICATION` privilege — see `agent-briefs/cdc-service-spec.md` § 4. The role is added to `services.json` and `seed.sh`.

Observability tools that need read-only access (Grafana queries against per-service DBs) get their own AppRole + DB role with read-only group membership — same pattern.

## How services connect — local development

Local Aspire stack at `deploy/aspire/Program.cs` provisions a Postgres container with the same database names + owner roles via `init-postgres.sql`. Connection strings are injected by Aspire into each service's env (`ConnectionStrings__<service>`). No Vault on local — the static credentials work because the dev stack is isolated.

`deploy/compose/docker-compose.yml` mirrors this for plain `docker compose` workflows.

## Capacity, limits, alarms

| Metric | Current ceiling | Headroom |
|---|---|---|
| `max_connections` | typically 100 on default Fly Postgres | tight under all-services-active load — PgBouncer in transaction mode mitigates |
| `max_replication_slots` | default 10 | bump to 20 before CDC rollout (one slot per logical DB + headroom for ad-hoc replays) |
| `max_wal_senders` | default 10 | bump to 20 same time as replication slots |
| `shared_buffers`, `work_mem` | Fly defaults | tune if hot-path query latency spikes |
| Disk | provisioned per Fly app config | snapshot before any size-altering migration |

Alarms (today: Fly's built-in dashboard; future: Prometheus per K8s spec):
- Connection pool utilisation > 80%
- Disk used > 75%
- Long-running query > 30s
- Replication slot lag > 100MB (post-CDC rollout)

## Migrations and schema management

Each service owns its own EF migrations under `src/<Service>/<Service>.Infrastructure/Migrations/`. Migrations apply on service boot via `db.Database.MigrateAsync()` in each Program.cs. There's no centralised migration pipeline — each service's migrations are scoped to its own logical DB.

This means a service's deploy can roll out independently as long as its migrations don't break a contract another service consumes (events, shared schemas — none today since each DB is logically isolated).

## Future plan: per-service CNPG clusters

`agent-briefs/k8s-platform-spec.md` § 12 (P2 — stateful services) targets a CloudNativePG cluster per service when the platform moves to Kubernetes. Migration plan:

1. Each service gets its own `infra/stateful/postgres-clusters/<service>.yaml` (Cluster CR).
2. WAL backups continue to S3-compatible storage via CNPG's plugin (works on any cloud + MinIO on-prem).
3. For the cutover: `pg_dump` the logical DB from Fly → restore into the new CNPG cluster → flip the Vault `database/config/<service>` connection string → service picks up new host on next credential refresh (≤ 1h).

The cutover is per-service, not big-bang — services migrate one at a time, each on its own schedule.

## Disaster recovery

| Scenario | Recovery |
|---|---|
| Single-row corruption | Vault replication audit log + per-service migration history can rebuild specific rows. PITR via Fly snapshot if widespread. |
| DB instance lost | Fly snapshot restore (max 24h data loss with current snapshot cadence). |
| Region outage (`iad`) | Currently no cross-region replica. Plan: secondary in `lhr` once CNPG migration unblocks streaming replication. |
| Vault unsealed but DB lost | Vault metadata is in the `postgres` DB — requires DB restore, then Vault unseal. Vault keys persist via `infra/vault/.init.json` staging. |
| Wrong role granted | Vault revokes all dynamic creds on next rotation cycle; SCM-tracked `services.json` is the source of truth. |

## Why everything is on one instance — and when to split

Single instance is simpler, cheaper, and good enough at the current scale. The trade-offs that argue for splitting (per-service CNPG cluster):

1. **Blast radius**: a runaway query in catalog can starve identity. Today, mitigated via `statement_timeout` per role + connection-pool tuning; long-term, physical separation removes the risk entirely.
2. **Independent scaling**: search-heavy reads from catalog don't need the same hardware as low-traffic identity. CNPG per service lets each scale independently.
3. **Independent upgrade cycles**: Postgres major version upgrade affects every service today. Per-cluster, each service can move on its own schedule.
4. **Compliance scoping**: payments + audit may have stricter compliance posture. Physical separation simplifies the audit envelope.

**Trigger to split**: any of (a) a service's tables hit single-instance scale ceilings, (b) the K8s migration ships, (c) a compliance audit demands separation. Until then, single-instance is the right answer.

## Reference files

- `deploy/aspire/init-postgres.sql` — the source of truth for which logical databases exist + owner-role pattern
- `deploy/vault/seed.sh` — how Vault is configured to issue per-service DB credentials against this Postgres
- `deploy/vault/README.md` — Vault setup walk-through
- `infra/vault/services.json` — list of (service, AppRole, DB role) tuples
- `agent-briefs/cdc-service-spec.md` § 4 — how CDC plugs into this topology
- `agent-briefs/k8s-platform-spec.md` § 12 P2 — the CNPG migration target
