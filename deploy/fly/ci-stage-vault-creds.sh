#!/usr/bin/env bash
# Capture vault's init keys + a fresh AppRole secret_id for EVERY service
# in infra/vault/services.json and stage them as Fly secrets on each
# matching app. Designed to be called from CI (.github/workflows/deploy.yml's
# stage-vault-creds job) but safe to run from a developer laptop too.
#
# Uses vault's HTTP API via curl rather than the vault CLI — the CLI
# behaves badly under non-TTY environments (CI), but curl returns
# deterministic JSON that jq can parse reliably.
#
# What it does:
#   1. Wait for vault to be unsealed + active.
#   2. If /vault/data/.init.json is present (only on first-ever boot),
#      read unseal_keys_b64[0] + root_token; stage them on the vault app.
#   3. With the root token in hand, for each service in services.json:
#        a. Fetch role_id from auth/approle/role/haworks-<svc>/role-id
#        b. Issue a *response-wrapped* secret_id (X-Vault-Wrap-TTL: 300)
#        c. Stage the wrapping_token (NOT the raw secret_id) as
#           Vault__SecretId on haworks-<svc>, plus role_id +
#           Vault__SecretIdIsWrapped=true so the bootstrap library
#           knows to unwrap on first boot.
#        d. ::add-mask:: every secret value before any log line so a
#           CI log leak window is 5 minutes max (the wrapper TTL).
#
# Idempotent. Safe to re-run on every deploy.
#
# Required env:
#   FLY_API_TOKEN — set in CI; also set in dev shell.
#
# Optional env:
#   VAULT_APP            — default "haworks-vault"
#   SERVICES_JSON        — default "infra/vault/services.json"
#   FLY_APP_PREFIX       — default "haworks-"
#   WRAP_TTL_SECONDS     — default 1800 (30min). Sized to cover the worst-
#                          case deploy lag: ci-stage-vault-creds runs once
#                          before deploy-backends starts; a slow service
#                          may not boot + try to unwrap until 10+min later.
#                          5min would expire mid-deploy.
set -euo pipefail

VAULT_APP="${VAULT_APP:-haworks-vault}"
SERVICES_JSON="${SERVICES_JSON:-infra/vault/services.json}"
FLY_APP_PREFIX="${FLY_APP_PREFIX:-haworks-}"
WRAP_TTL_SECONDS="${WRAP_TTL_SECONDS:-1800}"

log() { echo "[stage-vault-creds] $*"; }

# Mask a secret value in GitHub Actions logs BEFORE any echo of it.
# Outside CI (no GITHUB_ACTIONS env), this is a no-op echo to /dev/null.
# Always prefer this over `echo "${value:0:8}..."` truncation — partial
# disclosure of high-entropy secrets is still secret material.
mask() {
  if [[ "${GITHUB_ACTIONS:-false}" == "true" ]]; then
    echo "::add-mask::$1"
  fi
}

# Map vault service name → Fly app name. Most are 1:1 with the
# haworks- prefix; two have historical aliases.
fly_app_for_service() {
  case "$1" in
    checkout-orchestrator) echo "${FLY_APP_PREFIX}checkout" ;;
    bff-web)               echo "${FLY_APP_PREFIX}bffweb"   ;;
    *)                     echo "${FLY_APP_PREFIX}$1"        ;;
  esac
}

# Run a command inside the vault container via flyctl ssh. Returns the
# stdout (with the "Connecting to ..." prefix line stripped). Uses sed
# rather than `grep -v` because grep -v exits 1 when no lines match,
# which combined with pipefail kills the wait loop on any short response.
fly_ssh() {
  flyctl ssh console -a "$VAULT_APP" -C "$1" \
    | tr -d '\r' \
    | sed '/^Connecting to/d'
}

# ---------------------------------------------------------------------------
# Pre-flight: services.json must exist + parse cleanly. CI runs from repo
# root, dev runs may run from anywhere — find the file relative to the
# script if the default relative path doesn't resolve.
# ---------------------------------------------------------------------------
if [[ ! -f "$SERVICES_JSON" ]]; then
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  alt="$script_dir/../../infra/vault/services.json"
  if [[ -f "$alt" ]]; then
    SERVICES_JSON="$alt"
  else
    log "ERROR: services.json not found at $SERVICES_JSON or $alt"
    exit 1
  fi
fi

if ! jq -e '.services | length > 0' "$SERVICES_JSON" >/dev/null; then
  log "ERROR: $SERVICES_JSON has no services array"
  exit 1
fi

# ---------------------------------------------------------------------------
# Wait for vault to be FULLY READY: initialized + unsealed + active.
# A sealed-but-listening vault would let the listener check pass but
# return 503 from auth/approle endpoints. /v1/sys/health returns 200
# only when initialized + unsealed + active (default behavior).
# ---------------------------------------------------------------------------
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

# ---------------------------------------------------------------------------
# Step 1: capture init keys (only present on first-ever boot).
# ---------------------------------------------------------------------------
log "checking for /vault/data/.init.json..."
init_json=$(fly_ssh 'sh -c "cat /vault/data/.init.json 2>/dev/null || echo NOFILE"')
root_token=""
if [[ "$init_json" != "NOFILE" ]] && echo "$init_json" | jq -e '.unseal_keys_b64' >/dev/null 2>&1; then
  unseal_key=$(echo "$init_json" | jq -r '.unseal_keys_b64[0]')
  root_token=$(echo "$init_json" | jq -r '.root_token')
  if [[ -n "$unseal_key" && "$unseal_key" != "null" ]]; then
    log "creating CI deployer policy and token..."
    fly_ssh "sh -c \"cat <<EOF > /tmp/ci-policy.hcl
path \\\"auth/approle/role/*/role-id\\\" { capabilities = [\\\"read\\\"] }
path \\\"auth/approle/role/*/secret-id\\\" { capabilities = [\\\"update\\\"] }
EOF
curl -fsS -X PUT -H 'X-Vault-Token: $root_token' --data-binary @/tmp/ci-policy.hcl http://[::1]:8200/v1/sys/policies/acl/ci-deployer\"" >/dev/null

    ci_token_json=$(fly_ssh "sh -c \"curl -fsS -X POST -H 'X-Vault-Token: $root_token' -d '{\\\"policies\\\": [\\\"ci-deployer\\\"], \\\"period\\\": \\\"720h\\\"}' http://[::1]:8200/v1/auth/token/create\"")
    ci_token=$(echo "$ci_token_json" | jq -r '.auth.client_token')

    mask "$unseal_key"
    mask "$ci_token"
    log "staging VAULT_UNSEAL_KEY + VAULT_CI_TOKEN on $VAULT_APP"
    flyctl secrets set --stage -a "$VAULT_APP" \
      "VAULT_UNSEAL_KEY=$unseal_key" \
      "VAULT_CI_TOKEN=$ci_token" >/dev/null

    log "revoking root token..."
    fly_ssh "sh -c \"curl -fsS -X POST -H 'X-Vault-Token: $root_token' http://[::1]:8200/v1/auth/token/revoke-self\"" >/dev/null || true

    root_token="$ci_token"
  fi
else
  log "no .init.json on disk (already captured in a prior run, or vault is sealed)"
fi

# Need a root token or CI token. Prefer the one we just captured; fall back to env.
if [[ -z "$root_token" ]]; then
  log "no token in this run's context — fetching VAULT_CI_TOKEN from vault env"
  root_token=$(fly_ssh 'sh -c "printenv VAULT_CI_TOKEN"' | head -1)
  [[ -n "$root_token" ]] && mask "$root_token"
fi

if [[ -z "$root_token" || "$root_token" == "null" ]]; then
  log "ERROR: no CI token available — cannot capture AppRole creds."
  log "       Re-run after VAULT_CI_TOKEN propagates (one redeploy)."
  exit 1
fi

# ---------------------------------------------------------------------------
# Step 2: per-service AppRole creds with response-wrapped secret_ids.
#
# The wrapping token is what we stage to Fly. The service's bootstrap
# library unwraps it on first boot to get the actual secret_id (single-
# use, 5-min TTL). A leaked CI log only exposes the wrapper, which is
# useless after 5 minutes or after one unwrap (whichever comes first).
# ---------------------------------------------------------------------------
service_count=$(jq -r '.services | length' "$SERVICES_JSON")
i=0
deployed_count=0
skipped_count=0

while [[ "$i" -lt "$service_count" ]]; do
  svc=$(jq -r ".services[$i].name" "$SERVICES_JSON")
  fly_app=$(fly_app_for_service "$svc")
  role_name="haworks-$svc"

  i=$((i + 1))

  # Soft-skip when the Fly app doesn't exist yet (chicken-and-egg: app
  # must be created before first creds-stage). Keeps the workflow green
  # for partial deploys.
  if ! flyctl status -a "$fly_app" >/dev/null 2>&1; then
    log "skip $svc — Fly app $fly_app not deployed yet"
    skipped_count=$((skipped_count + 1))
    continue
  fi

  log "fetching role_id for $role_name"
  role_id_json=$(fly_ssh "sh -c \"curl -fsS -H 'X-Vault-Token: $root_token' http://[::1]:8200/v1/auth/approle/role/$role_name/role-id\"")
  role_id=$(echo "$role_id_json" | jq -r '.data.role_id // empty')

  if ! echo "$role_id" | grep -qE '^[0-9a-f-]{36}$'; then
    log "ERROR ($svc): role_id not UUID-shaped. AppRole likely missing — re-run vault deploy to seed."
    log "             Vault response: $role_id_json"
    exit 1
  fi

  # Issue a response-wrapped secret_id. The X-Vault-Wrap-TTL header tells
  # vault to wrap the response in a single-use token instead of returning
  # the raw secret_id. Service unwraps on first boot.
  log "issuing wrapped secret_id for $role_name (TTL=${WRAP_TTL_SECONDS}s)"
  wrap_resp=$(fly_ssh "sh -c \"curl -fsS -X POST -H 'X-Vault-Token: $root_token' -H 'X-Vault-Wrap-TTL: $WRAP_TTL_SECONDS' http://[::1]:8200/v1/auth/approle/role/$role_name/secret-id\"")
  wrapping_token=$(echo "$wrap_resp" | jq -r '.wrap_info.token // empty')

  # Vault wrapping tokens are hvs.* (since Vault 1.10) — be defensive but
  # don't gate on the prefix in case format changes; just check it's not
  # empty and looks token-shaped.
  if [[ -z "$wrapping_token" || "$wrapping_token" == "null" ]]; then
    log "ERROR ($svc): wrap response missing wrap_info.token. Response: $wrap_resp"
    exit 1
  fi

  mask "$wrapping_token"
  # role_id is technically half of the credential pair (both role_id AND
  # secret_id are required to login under AppRole), but defense-in-depth:
  # mask it anyway since CI logs are forever.
  mask "$role_id"

  log "staging Vault__RoleId + wrapped Vault__SecretId on $fly_app"
  flyctl secrets set --stage -a "$fly_app" \
    "Vault__RoleId=$role_id" \
    "Vault__SecretId=$wrapping_token" \
    "Vault__SecretIdIsWrapped=true" >/dev/null

  deployed_count=$((deployed_count + 1))
done

log "done. staged $deployed_count services, skipped $skipped_count (no Fly app yet)."
