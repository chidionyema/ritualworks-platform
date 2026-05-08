#!/bin/sh
# Identity entrypoint shim — fetches the AppRole role_id + secret_id from
# Vault at startup so the .NET service can read them from disk (the path
# VaultConfigBootstrap expects).
#
# Required env when Vault__Enabled=true:
#   Vault__Address       — e.g. http://ritualworks-vault.internal:8200
#   VAULT_ROOT_TOKEN     — one-shot token used only here, not by the app
#   Vault__RoleIdPath    — defaults to /tmp/vault/role_id
#   Vault__SecretIdPath  — defaults to /tmp/vault/secret_id
#
# When Vault__Enabled is anything other than "true", we skip the fetch
# and exec the app directly — useful for the current deploy state where
# identity boots without Vault.
# Note: no `set -e` — we *want* to fall through to the .NET app even if the
# vault fetch fails. A vault outage shouldn't take identity down with it; it
# just means demos that need vault won't work until vault recovers.

fail_open() {
  echo "[bootstrap] WARN: $1 — starting identity with Vault disabled"
  export Vault__Enabled=false
  unset VAULT_ROOT_TOKEN
  exec dotnet Identity.Api.dll
}

if [ "${Vault__Enabled:-false}" = "true" ]; then
  if [ -z "${Vault__Address:-}" ]; then
    fail_open "Vault__Address not set"
  fi
  if [ -z "${VAULT_ROOT_TOKEN:-}" ]; then
    fail_open "VAULT_ROOT_TOKEN not set"
  fi

  ROLE_ID_PATH="${Vault__RoleIdPath:-/tmp/vault/role_id}"
  SECRET_ID_PATH="${Vault__SecretIdPath:-/tmp/vault/secret_id}"
  mkdir -p "$(dirname "$ROLE_ID_PATH")" "$(dirname "$SECRET_ID_PATH")"

  # Wait up to 90s for Vault. Even though deploy.yml runs deploy-vault
  # before deploy-backends, Fly's 6PN DNS for the new vault machine can
  # take 30-60s to propagate to a fresh identity machine on the same
  # rollout. 90s covers that. Beyond it we give up and fail open.
  echo "[bootstrap] waiting for Vault at $Vault__Address..."
  vault_ready=0
  for i in $(seq 1 90); do
    if curl -fsS -o /dev/null "$Vault__Address/v1/sys/health"; then
      vault_ready=1
      break
    fi
    sleep 1
  done

  if [ "$vault_ready" != "1" ]; then
    fail_open "Vault did not respond within 90s at $Vault__Address"
  fi

  echo "[bootstrap] fetching bootstrap creds from secret/identity/bootstrap..."
  RESPONSE=$(curl -fsS \
    -H "X-Vault-Token: $VAULT_ROOT_TOKEN" \
    "$Vault__Address/v1/secret/data/identity/bootstrap" 2>/dev/null) \
    || fail_open "could not read secret/identity/bootstrap (vault token bad? seed not run?)"

  ROLE_ID=$(echo "$RESPONSE"   | jq -r '.data.data.role_id')
  SECRET_ID=$(echo "$RESPONSE" | jq -r '.data.data.secret_id')

  if [ -z "$ROLE_ID" ] || [ "$ROLE_ID" = "null" ]; then
    fail_open "role_id missing from Vault response"
  fi

  printf '%s' "$ROLE_ID"   > "$ROLE_ID_PATH"
  printf '%s' "$SECRET_ID" > "$SECRET_ID_PATH"
  chmod 600 "$ROLE_ID_PATH" "$SECRET_ID_PATH"

  # Drop the root token from the env so the .NET process never sees it.
  unset VAULT_ROOT_TOKEN

  echo "[bootstrap] wrote role_id to $ROLE_ID_PATH, secret_id to $SECRET_ID_PATH"
else
  echo "[bootstrap] Vault__Enabled is not 'true', skipping bootstrap"
fi

exec dotnet Identity.Api.dll
