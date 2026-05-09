#!/bin/sh
# Idempotent vault seed. Safe to run on every container start because
# all the operations either create-if-missing or overwrite with the
# same value.
#
# Sets up:
#   * AppRole auth method
#   * Policy haworks-identity-app
#   * AppRole role haworks-identity-app
#   * KV v2 placeholders identity needs at startup
#
# In prod-mode (raft storage on a persistent volume) the role's
# auto-generated role_id and secret_ids persist across container
# restarts — there's no need to register deterministic values like
# the dev-mode wipe-recovery dance required.
set -e

export VAULT_ADDR="${VAULT_ADDR:-http://127.0.0.1:8200}"

# VAULT_TOKEN must be set by the caller (entrypoint passes the root token).
if [ -z "${VAULT_TOKEN:-}" ]; then
  echo "[seed] ERROR: VAULT_TOKEN is required" >&2
  exit 1
fi

echo "[seed] enabling AppRole auth (if missing)..."
if ! vault auth list -format=json | jq -e '."approle/"' >/dev/null 2>&1; then
  vault auth enable approle
fi

echo "[seed] writing policy haworks-identity-app..."
# Identity needs create/update on secret/data/identity/* because
# VaultJwtSigningKeyProvider generates and persists the JWT signing
# key on first run if it's missing. Read-only crashed startup with
# "permission denied" on the first WriteSecretAsync call.
vault policy write haworks-identity-app - <<'POLICY'
path "secret/data/identity/*"     { capabilities = ["create", "read", "update", "patch", "list"] }
path "secret/metadata/identity/*" { capabilities = ["read", "list"] }
path "secret/data/*"              { capabilities = ["read", "list"] }
path "secret/metadata/*"          { capabilities = ["read", "list"] }
path "database/creds/*"           { capabilities = ["read"] }
path "auth/token/lookup-self"     { capabilities = ["read"] }
path "auth/token/renew-self"      { capabilities = ["update"] }
POLICY

echo "[seed] writing role haworks-identity-app..."
vault write auth/approle/role/haworks-identity-app \
  token_policies="haworks-identity-app" \
  token_ttl=1h \
  token_max_ttl=24h \
  secret_id_ttl=0 \
  bind_secret_id=true >/dev/null

# KV v2 lives at secret/ by default in vault. Ensure the mount exists
# (idempotent — vault returns 400 "path is already in use" if it does,
# which is fine).
if ! vault secrets list -format=json | jq -e '."secret/"' >/dev/null 2>&1; then
  vault secrets enable -path=secret -version=2 kv
fi

# Seed identity's required KV paths. These all use kv put which
# overwrites — safe to run on every boot, no-op when values match.
# Real OAuth credentials would come from a separate operator step;
# the placeholders let identity start cleanly with the conditional-
# registration code in Identity.Infrastructure (blank ClientId →
# provider not registered).
echo "[seed] writing identity KV placeholders..."
vault kv put secret/identity/jwt \
  signing_key="dev-only-not-for-prod" >/dev/null 2>&1 || true
for provider in google microsoft facebook; do
  vault kv put "secret/identity/oauth/$provider" \
    client_id="" client_secret="" >/dev/null 2>&1 || true
done

ROLE_ID=$(vault read -field=role_id auth/approle/role/haworks-identity-app/role-id)
echo "[seed] done. role_id=${ROLE_ID:0:8}..."

# ---------------------------------------------------------------------------
# Database secrets engine: dynamic short-TTL Postgres credentials.
#
# Vault issues per-request Postgres users for the haworks-identity role,
# scoped to a dedicated Fly Postgres app (ritualworks-vault-pg). The
# operator credentials Vault uses to CREATE ROLE on demand are supplied
# via env vars staged by deploy/fly/bootstrap.sh:
#
#   VAULT_PG_OPERATOR_USERNAME  (default "postgres")
#   VAULT_PG_OPERATOR_PASSWORD  (REQUIRED — no default; skip block if unset)
#
# When VAULT_PG_OPERATOR_PASSWORD is unset (e.g. demo Postgres not yet
# deployed) we log a warning and skip — the AppRole/KV setup above is
# still complete, so identity can boot with static secrets only.
# ---------------------------------------------------------------------------
VAULT_PG_OPERATOR_USERNAME="${VAULT_PG_OPERATOR_USERNAME:-postgres}"

if [ -z "${VAULT_PG_OPERATOR_PASSWORD:-}" ]; then
  echo "[seed] WARN: VAULT_PG_OPERATOR_PASSWORD not set — skipping database"
  echo "[seed]       secrets engine setup. Stage the secret via bootstrap.sh"
  echo "[seed]       once ritualworks-vault-pg is deployed, then redeploy."
else
  echo "[seed] enabling database secrets engine (if missing)..."
  if ! vault secrets list -format=json | jq -e '."database/"' >/dev/null 2>&1; then
    vault secrets enable database
  fi

  # Configure the connection. `vault write database/config/<name>` is an
  # upsert: re-running with the same values is a no-op, and rotated
  # operator passwords flow through on next boot. allowed_roles must be
  # set explicitly — vault refuses to issue creds for a role unless its
  # name is in the connection's allow-list.
  echo "[seed] configuring database connection vault-pg..."
  vault write database/config/vault-pg \
    plugin_name=postgresql-database-plugin \
    allowed_roles="haworks-identity" \
    connection_url="postgresql://{{username}}:{{password}}@ritualworks-vault-pg.internal:5432/postgres?sslmode=disable" \
    username="$VAULT_PG_OPERATOR_USERNAME" \
    password="$VAULT_PG_OPERATOR_PASSWORD" >/dev/null

  # Define the dynamic role. {{name}} and {{password}} are vault's
  # standard template variables — vault substitutes a unique generated
  # username (prefixed with the role name) and a strong random password
  # at lease-creation time. default_ttl=10m keeps blast radius small;
  # max_ttl=1h caps lease renewals so even a stuck client can't hold a
  # credential indefinitely.
  echo "[seed] writing database role haworks-identity..."
  vault write database/roles/haworks-identity \
    db_name=vault-pg \
    creation_statements="CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}'; GRANT CONNECT ON DATABASE postgres TO \"{{name}}\"; GRANT USAGE ON SCHEMA public TO \"{{name}}\";" \
    default_ttl="10m" \
    max_ttl="1h" >/dev/null

  echo "[seed] database engine ready. Verify with:"
  echo "[seed]   vault read database/creds/haworks-identity"
fi
