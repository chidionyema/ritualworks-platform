# ADR-0004: Database-per-Service in a Shared PostgreSQL Cluster

**Status:** Accepted
**Date:** 2026-05-02
**Deciders:** chidionyema

## Context

The monolith has 5 PostgreSQL schemas (catalog, orders, payments, content, identity) on one Postgres instance, owned by 5 EF Core DbContexts. Cross-context queries are technically possible because everything is one connection string.

Post-migration, each service must own its data. The choice is the **physical topology**: how isolated does "owns its data" need to be?

## Decision

**Each service gets its own logical PostgreSQL database in a shared cluster** (one cluster per environment: dev / staging / prod).

- Hard isolation: separate Postgres user, separate connection string, separate `__EFMigrationsHistory`.
- Cross-DB queries blocked by default — `postgres_fdw` is not installed.
- One operational footprint: one HA cluster, one backup pipeline, one PITR config.

Locally (Aspire): `builder.AddPostgres("postgres")` + 7 `AddDatabase()` calls. Production (kind/EKS): one managed cluster (RDS / Aurora / Cloud SQL) with 7 databases + 7 roles, each with its own dynamically-rotated credentials via Vault.

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| Schema-per-service in one DB | Easiest migration from current state. | This IS the current monolith. Connection pools, vacuum, WAL, role grants all shared. Boundary is honor-system. Any service can `SELECT` across schemas. Fails the bounded-context rule. | Rejected. Defeats the migration purpose. |
| **Database-per-service, shared cluster (chosen)** | Hard isolation: separate user, separate conn string, separate `__EFMigrationsHistory`. No cross-DB queries possible by default. One cluster to operate. | Backup/restore per database is a per-DB operation. | **Chosen.** Best balance of isolation and operational simplicity. |
| Separate Postgres instance per service | Maximum isolation — one service's bad query can't affect another. | 7 services × prod/staging/dev × HA replica = ~42 Postgres instances. The cluster is not the bottleneck today. Path to instance-per-service is "promote one DB out of the shared cluster" — trivially done later if a single service's IO/CPU profile demands it. | Rejected as premature. |
| Shared DB, no schema separation (single-user) | Smallest infra. | Worse than the current monolith. | Rejected. |

## Consequences

### Positive
- True data isolation — no service can sneak a JOIN into another service's tables.
- One Postgres cluster to operate, monitor, back up.
- Vault dynamic credentials work cleanly per-database (current `DynamicCredentialsConnectionInterceptor` extends naturally).
- Easy to promote one DB out of the shared cluster later (Catalog, with heavy search workload, is the likely first candidate).
- EF migrations per service: `MigrateAllAsync` discovers exactly one DbContext per service, no code change needed.

### Negative
- "Need data from another service" requires going via events (replicated read model) or sync gRPC. **Mitigation:** this is the desired behavior — see [01-architecture.md § Cross-service data access](../01-architecture.md#cross-service-data-access--three-patterns-in-order-of-preference).
- Cross-DB foreign keys (today: `orders.UserId → identity.AspNetUsers.Id`, `payments.OrderId → orders.Orders.Id`) must be removed before Phase 5. **Mitigation:** pre-Phase-5 inventory PR + `CrossDbForeignKeyAuditTests` that fails CI on any remaining cross-DB FK.
- One cluster failure takes down all services. **Mitigation:** managed cluster with HA + read replicas; same as today's monolith blast radius (which is acceptable for portfolio scope).

### Neutral
- Connection pooling is per-database. Each service's PgPool sizing is independent. **Tune at deploy time.**

## Notes

The cross-service data access patterns this enables:

1. **Default — events carry the data.** `OrderCreatedEvent` already carries `TotalAmount`, `Currency`, `CustomerEmail`, `OrderLineItems`. Consumer never calls back to producer. Events are the data contract.
2. **Replicated read model (CQRS).** Subscribe to `Catalog.ProductUpdated`, build local `catalog_product_snapshots` table in Orders DB. Eventually consistent, owned by Orders.
3. **Sync gRPC** — only for freshness-critical authority checks. Examples: "is this user permitted right now" → identity-svc. "current price at moment of charge" → catalog-svc.

The Identity / UserId problem is solved by treating `UserId` as an opaque foreign key — no cross-DB constraint, no JOIN. Profile data flows via `UserProfileChanged` events into per-service `user_snapshots` tables.

Reference: [01-architecture.md § Data Strategy](../01-architecture.md#data-strategy)
