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
