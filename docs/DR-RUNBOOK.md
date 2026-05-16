# Disaster Recovery Runbook — Haworks Platform

## 1. Overview
This runbook defines the procedures for recovering the Haworks Platform from critical infrastructure failure or data loss.

**RPO (Recovery Point Objective):** 5 minutes (via WAL-G / Neon PITR)
**RTO (Recovery Time Objective):** 2 hours

## 2. Infrastructure Dependencies
1. **Neon Postgres:** Primary database (managed)
2. **HashiCorp Vault:** Secrets management (self-hosted on Fly)
3. **Redis:** Distributed caching and SignalR backplane
4. **RabbitMQ:** Asynchronous messaging
5. **Elasticsearch:** Search and Geospatial indexing

## 3. Data Recovery Procedures

### 3.1 Postgres (Neon)
Neon manages automated backups and Point-In-Time Recovery (PITR).
- **Procedure:** 
  1. Log into Neon Console.
  2. Select the branch and click "Restore to a specific point in time".
  3. Select the desired timestamp.
  4. Update connection strings in Fly secrets if a new branch was created.

### 3.2 Vault
Vault data is stored in a dedicated Postgres database on Fly.
- **Procedure:**
  1. Restore the `vault-pg` database from Fly volume snapshots.
  2. Restart the Vault service instances.
  3. Perform the unseal process using the shamir keys stored in the secure offline location.

### 3.3 Elasticsearch
Elasticsearch acts as a read-projection.
- **Procedure:**
  1. Clear the Elasticsearch indices.
  2. Trigger a full re-index from the `Catalog` and `Merchant` services.

## 4. Service Recovery Order
Services must be restarted in the following order to ensure dependencies are available:
1. **Infrastructure:** Vault, Postgres, Redis, RabbitMQ
2. **Core Services:** Identity, Localization
3. **Domain Services:** Catalog, Merchant, Location
4. **Orchestrators:** CheckoutOrchestrator, Orders
5. **Edge:** BffWeb

## 5. Verification Checklist
- [ ] Identity health checks return 200 OK.
- [ ] Vault is unsealed and reachable.
- [ ] MassTransit consumers are connected to RabbitMQ.
- [ ] Catalog data is searchable via Elasticsearch.
- [ ] E2E smoke tests pass against the restored environment.
