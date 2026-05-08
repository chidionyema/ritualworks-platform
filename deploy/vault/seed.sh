#!/bin/sh
# Idempotent Vault seed. Safe to run on every Vault container start.
#
# Sets up:
#   * KV v2 at secret/   (already enabled in dev-mode; we just verify)
#   * AppRole auth method
#   * Policy haworks-identity-app
#   * AppRole role haworks-identity-app with that policy
#   * Fresh secret_id every run, stashed at secret/identity/bootstrap
#     alongside the role_id so identity can self-fetch on boot.
set -e

export VAULT_ADDR="${VAULT_ADDR:-http://127.0.0.1:8200}"
export VAULT_TOKEN="${VAULT_TOKEN:-$VAULT_DEV_ROOT_TOKEN_ID}"

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

ROLE_ID=$(vault read -format=json auth/approle/role/haworks-identity-app/role-id \
  | jq -r '.data.role_id')
SECRET_ID=$(vault write -force -format=json auth/approle/role/haworks-identity-app/secret-id \
  | jq -r '.data.secret_id')

# Stash both at a well-known KV path. The identity service reads this with
# its VAULT_ROOT_TOKEN at startup, writes the values to disk, then auths
# normally via AppRole — the root token is only used for this one bootstrap
# fetch and never persists past the entrypoint shim.
echo "[seed] writing bootstrap creds to secret/identity/bootstrap..."
vault kv put secret/identity/bootstrap \
  role_id="$ROLE_ID" \
  secret_id="$SECRET_ID" >/dev/null

# Seed a placeholder identity JWT secret so the demo's vault-status probe
# has something to read after auth completes. Real values would come from
# a CI step or operator after deploy.
vault kv put secret/identity/jwt \
  signing_key="dev-only-not-for-prod-$(date +%s)" >/dev/null 2>&1 || true

# Identity's VaultConfigBootstrap (Program.cs) reads secret/identity/oauth/*
# at startup and rethrows on any 404. The Identity.Infrastructure
# conditional-registration code tolerates *blank* OAuth credentials, but
# the read itself must succeed. Write empty placeholders so identity
# starts cleanly without OAuth providers wired (the actual registration
# fix lives in Identity.Infrastructure.DependencyInjection).
for provider in google microsoft facebook; do
  vault kv put "secret/identity/oauth/$provider" \
    client_id="" client_secret="" >/dev/null 2>&1 || true
done

echo "[seed] done. role_id=${ROLE_ID:0:8}... (full value at secret/identity/bootstrap)"
