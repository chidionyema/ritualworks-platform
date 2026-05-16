# R1 — Vault Engine Enablement

**Brief:** R1 | **Spec:** `docs/agent-briefs/secret-rotation-spec.md`
**Phase:** 1 (sequential — blocks R2–R6)
**Time budget:** 30 min

---

## Inputs

Read before touching anything:
- `deploy/vault/vault.hcl` — Raft storage, IPv6 listener, TLS disabled
- `infra/vault/database/roles.json` — 7 static roles, plugin name, rotation_period
- `infra/vault/services.json` — 8 services, has_db flags
- `deploy/vault/seed.sh` — existing Vault init sequence
- `deploy/vault/entrypoint.sh` — container entrypoint

---

## Deliverable

Modify `deploy/vault/seed.sh` to add (after existing KV mounts):

1. **Database secrets engine** — enable if not already enabled:
   ```bash
   vault secrets enable -path=database database 2>/dev/null || true
   vault write database/config/postgres-haworks \
     plugin_name=postgresql-database-plugin \
     connection_url="postgresql://{{username}}:{{password}}@${POSTGRES_HOST}:5432/postgres?sslmode=disable" \
     allowed_roles="haworks-identity,haworks-catalog,haworks-orders,haworks-payments,haworks-content,haworks-checkout-orchestrator,haworks-notifications" \
     username="${POSTGRES_ROOT_USER}" \
     password="${POSTGRES_ROOT_PASSWORD}"
   ```

2. **Static role registration** for all 7 roles in `infra/vault/database/roles.json`:
   ```bash
   vault write database/static-roles/haworks-identity \
     db_name=postgres-haworks \
     username=identity_owner \
     rotation_period=3600   # 1 hour
   ```
   Repeat for all 7 roles using data from `roles.json`.

3. **PKI engine**:
   ```bash
   vault secrets enable pki 2>/dev/null || true
   vault secrets tune -max-lease-ttl=8760h pki
   vault write -field=certificate pki/root/generate/internal \
     common_name="haworks-internal-ca" \
     ttl=8760h \
     key_type=ec \
     key_bits=384 > /dev/null
   vault write pki/config/urls \
     issuing_certificates="http://haworks-vault.internal:8200/v1/pki/ca" \
     crl_distribution_points="http://haworks-vault.internal:8200/v1/pki/crl"
   ```

4. **PKI role per service** (for all services in `services.json`):
   ```bash
   vault write pki/roles/haworks-identity \
     allowed_domains="haworks-identity.internal" \
     allow_subdomains=false \
     max_ttl=24h \
     key_type=ec \
     key_bits=256
   ```

5. **Policy updates** — update `infra/vault/policies/service-template.hcl.tmpl` to add:
   ```hcl
   # Database credential rotation
   path "database/static-creds/{{service}}" {
     capabilities = ["read"]
   }
   # PKI cert issuance
   path "pki/issue/{{service}}" {
     capabilities = ["create", "update"]
   }
   ```

---

## Acceptance

```bash
# In a local vault dev container:
vault status                                          # should show sealed: false
vault read database/config/postgres-haworks          # should show plugin_name
vault read database/static-roles/haworks-identity    # should show rotation_period=3600
vault read pki/roles/haworks-identity                # should show max_ttl=24h
./deploy/vault/seed.sh && echo "SEED OK"             # idempotent — safe to run twice
```

---

## Anti-stuck

- Do not modify `deploy/vault/vault.hcl` — TLS/Raft config is production-correct.
- All role names come from `infra/vault/database/roles.json` — do not invent new ones.
- `vault secrets enable` must use `2>/dev/null || true` pattern — engine already enabled is not an error.
- `POSTGRES_HOST`, `POSTGRES_ROOT_USER`, `POSTGRES_ROOT_PASSWORD` are environment variables injected by Docker Compose. Do not hardcode values.
- If `seed.sh` currently sources env vars from a file, keep that pattern — do not change the sourcing mechanism.

---

## Done-report format

```
brief: R1
status: done | blocked
files_changed:
  - deploy/vault/seed.sh
  - infra/vault/policies/service-template.hcl.tmpl
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
