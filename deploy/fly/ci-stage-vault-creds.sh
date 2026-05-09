#!/usr/bin/env bash
# Capture vault's init keys + a fresh AppRole secret_id and stage them
# as Fly secrets on the relevant apps. Designed to be called from CI
# (.github/workflows/deploy.yml's stage-vault-creds job) but safe to
# run from a developer laptop too.
#
# Uses vault's HTTP API via curl rather than the vault CLI — the CLI
# behaves badly under non-TTY environments (CI), but curl returns
# deterministic JSON that jq can parse reliably.
#
# What it does:
#   1. Wait for vault's HTTP listener to come up.
#   2. If /vault/data/.init.json is present (only on first-ever boot),
#      read unseal_keys_b64[0] + root_token; stage them on the vault app.
#   3. With the root token in hand, fetch role_id from
#      auth/approle/role/<role>/role-id and write a fresh secret_id;
#      stage both as Vault__RoleId / Vault__SecretId on identity.
#
# Idempotent. Safe to re-run on every deploy.
#
# Required env:
#   FLY_API_TOKEN — set in CI; also set in dev shell.
#
# Optional env:
#   VAULT_APP      — default "ritualworks-vault"
#   IDENTITY_APP   — default "ritualworks-identity"
#   ROLE_NAME      — default "haworks-identity-app"
set -euo pipefail

VAULT_APP="${VAULT_APP:-ritualworks-vault}"
IDENTITY_APP="${IDENTITY_APP:-ritualworks-identity}"
ROLE_NAME="${ROLE_NAME:-haworks-identity-app}"

log() { echo "[stage-vault-creds] $*"; }

# Run a command inside the vault container via flyctl ssh. Returns the
# stdout (with the "Connecting to ..." prefix line stripped). Uses sed
# rather than `grep -v` because grep -v exits 1 when no lines match,
# which combined with pipefail kills the wait loop on any short response.
fly_ssh() {
  flyctl ssh console -a "$VAULT_APP" -C "$1" \
    | tr -d '\r' \
    | sed '/^Connecting to/d'
}

# Wait for vault to be FULLY READY: initialized + unsealed + active.
# A sealed-but-listening vault would let the listener check pass but
# return 503 from auth/approle endpoints, racing the script ahead of
# the entrypoint's unseal step. /v1/sys/health returns 200 only when
# the node is initialized, unsealed, and active (default behavior).
log "waiting for vault to be unsealed + active on $VAULT_APP..."
ready=0
for i in $(seq 1 60); do
  if fly_ssh 'sh -c "curl -fsS -o /dev/null http://[::1]:8200/v1/sys/health"' >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 2
done
if [[ "$ready" != "1" ]]; then
  log "ERROR: vault never reached active+unsealed within 120s — check the vault entrypoint log."
  exit 1
fi

# Step 1: capture init keys (only present on first-ever boot).
log "checking for /vault/data/.init.json..."
init_json=$(fly_ssh 'sh -c "cat /vault/data/.init.json 2>/dev/null || echo NOFILE"')
if [[ "$init_json" != "NOFILE" ]] && echo "$init_json" | jq -e '.unseal_keys_b64' >/dev/null 2>&1; then
  unseal_key=$(echo "$init_json" | jq -r '.unseal_keys_b64[0]')
  root_token=$(echo "$init_json" | jq -r '.root_token')
  if [[ -n "$unseal_key" && "$unseal_key" != "null" ]]; then
    log "staging VAULT_UNSEAL_KEY + VAULT_ROOT_TOKEN_PROD on $VAULT_APP"
    flyctl secrets set --stage -a "$VAULT_APP" \
      "VAULT_UNSEAL_KEY=$unseal_key" \
      "VAULT_ROOT_TOKEN_PROD=$root_token" >/dev/null
  fi
else
  log "no .init.json on disk (already captured in a prior run, or vault is sealed)"
fi

# Step 2: AppRole role_id + fresh secret_id via vault HTTP API.
# Need a root token. Prefer the one we just captured; fall back to env.
if [[ -z "${root_token:-}" ]]; then
  log "no root token in this run's context — fetching from vault env"
  root_token=$(fly_ssh 'sh -c "printenv VAULT_ROOT_TOKEN_PROD"' | head -1)
fi

if [[ -z "${root_token:-}" || "$root_token" == "null" ]]; then
  log "ERROR: no root token available — cannot capture AppRole creds."
  log "       Re-run after VAULT_ROOT_TOKEN_PROD propagates (one redeploy)."
  exit 1
fi

log "fetching role_id via HTTP API"
role_id_json=$(fly_ssh "sh -c \"curl -fsS -H 'X-Vault-Token: $root_token' http://[::1]:8200/v1/auth/approle/role/$ROLE_NAME/role-id\"")
role_id=$(echo "$role_id_json" | jq -r '.data.role_id // empty')

log "writing fresh secret_id via HTTP API"
secret_id_json=$(fly_ssh "sh -c \"curl -fsS -X POST -H 'X-Vault-Token: $root_token' http://[::1]:8200/v1/auth/approle/role/$ROLE_NAME/secret-id\"")
secret_id=$(echo "$secret_id_json" | jq -r '.data.secret_id // empty')

if ! echo "$role_id" | grep -qE '^[0-9a-f-]{36}$'; then
  log "ERROR: role_id ('${role_id:0:30}...') is not UUID-shaped."
  log "       Full vault response: $role_id_json"
  exit 1
fi
if ! echo "$secret_id" | grep -qE '^[0-9a-f-]{36}$'; then
  log "ERROR: secret_id ('${secret_id:0:30}...') is not UUID-shaped."
  log "       Full vault response: $secret_id_json"
  exit 1
fi

log "staging Vault__RoleId + Vault__SecretId on $IDENTITY_APP"
log "  role_id   = ${role_id:0:8}..."
log "  secret_id = ${secret_id:0:8}..."
flyctl secrets set --stage -a "$IDENTITY_APP" \
  "Vault__RoleId=$role_id" \
  "Vault__SecretId=$secret_id" >/dev/null

log "done. Identity will pick up new creds on next deploy."
