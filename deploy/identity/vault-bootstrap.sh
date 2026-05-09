#!/bin/sh
# Identity entrypoint shim. Two modes, in order of preference:
#
#  1. DIRECT (steady state) — Vault__RoleId and Vault__SecretId are
#     supplied as Fly secrets at bootstrap time. The shim is a no-op:
#     exec dotnet immediately, the .NET VaultConfigBootstrap reads the
#     creds straight from config. Identity startup has zero dependency
#     on vault availability or boot order.
#
#  2. LEGACY FETCH (first-ever bootstrap, before vault is deployed) —
#     VAULT_ROOT_TOKEN is set, role_id/secret_id are fetched from vault
#     and written to disk for VaultConfigBootstrap's path-based reader.
#     Falls open after a 30s wait so a vault outage doesn't crash-loop
#     identity.
#
# Note: no `set -e` — we *want* to fall through to the .NET app even if the
# vault fetch fails. A vault outage shouldn't take identity down with it; it
# just means demos that need vault won't work until vault recovers.

fail_open() {
  echo "[bootstrap] WARN: $1 — starting identity with Vault disabled"
  export Vault__Enabled=false
  unset VAULT_ROOT_TOKEN
  exec dotnet Identity.Api.dll
}

# Mode 1: direct creds present → no fetch needed. Most common path now
# that bootstrap.sh stages role_id/secret_id at deploy time. We still
# write them to disk because several downstream consumers
# (VaultClientFactory, VaultJwtSigningKeyProvider, etc) read from the
# path-based VaultOptions.RoleIdPath/SecretIdPath fields. The .NET app
# also reads them directly from Vault:RoleId/SecretId where it can
# (VaultConfigBootstrap, JWT signing key DI in Identity.Infrastructure),
# so this disk write is purely defensive belt-and-braces for the legacy
# path-based code paths.
if [ -n "${Vault__RoleId:-}" ] && [ -n "${Vault__SecretId:-}" ]; then
  echo "[bootstrap] Direct AppRole creds present — writing to disk for legacy path consumers"
  ROLE_ID_PATH="${Vault__RoleIdPath:-/tmp/vault/role_id}"
  SECRET_ID_PATH="${Vault__SecretIdPath:-/tmp/vault/secret_id}"
  mkdir -p "$(dirname "$ROLE_ID_PATH")" "$(dirname "$SECRET_ID_PATH")"
  printf '%s' "$Vault__RoleId"   > "$ROLE_ID_PATH"
  printf '%s' "$Vault__SecretId" > "$SECRET_ID_PATH"
  chmod 600 "$ROLE_ID_PATH" "$SECRET_ID_PATH"
  exec dotnet Identity.Api.dll
fi

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

  # Vault's seed.sh runs on every container start and takes ~30-60s for the
  # AppRole+policy steps before it writes secret/identity/bootstrap. If
  # identity comes up faster than that, the path will 404 briefly. Retry
  # for up to 90s before giving up.
  echo "[bootstrap] fetching bootstrap creds from secret/identity/bootstrap..."
  ROLE_ID=""
  SECRET_ID=""
  for attempt in $(seq 1 30); do
    RESPONSE=$(curl -fsS \
      -H "X-Vault-Token: $VAULT_ROOT_TOKEN" \
      "$Vault__Address/v1/secret/data/identity/bootstrap" 2>/dev/null) || RESPONSE=""

    if [ -n "$RESPONSE" ]; then
      ROLE_ID=$(echo "$RESPONSE"   | jq -r '.data.data.role_id // empty')
      SECRET_ID=$(echo "$RESPONSE" | jq -r '.data.data.secret_id // empty')
      if [ -n "$ROLE_ID" ]; then break; fi
    fi
    sleep 3
  done

  if [ -z "$ROLE_ID" ]; then
    fail_open "secret/identity/bootstrap not populated after 90s — seed.sh may be slow"
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
