#!/usr/bin/env bash
# Idempotent setup: creates Fly apps, allocates a public IP only on the BFF,
# auto-generates the Identity JWT keypair on first run, and stages per-service
# secrets read from deploy/fly/.env.local.
#
# Usage:
#   deploy/fly/bootstrap.sh                       # uses deploy/fly/.env.local
#   deploy/fly/bootstrap.sh path/to/other.env
#
# Re-run safely after editing .env.local — secrets are restaged.
# Secrets are staged (not deployed); deploy/fly/deploy.sh applies them.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="${1:-$ROOT_DIR/deploy/fly/.env.local}"

[[ -f "$ENV_FILE" ]] || {
  echo "Missing $ENV_FILE" >&2
  echo "Copy deploy/fly/.env.example to deploy/fly/.env.local and fill in." >&2
  exit 1
}

# shellcheck disable=SC1090
set -a; source "$ENV_FILE"; set +a

required=(RABBITMQ_URL REDIS_URL POSTGRES_BASE POSTGRES_QUERY)
for v in "${required[@]}"; do
  [[ -n "${!v:-}" ]] || { echo "Missing $v in $ENV_FILE" >&2; exit 1; }
done

REGION="${REGION:-iad}"
# DEPLOY_CONTENT gates the optional content service. Two places must agree:
#   1. .env.local DEPLOY_CONTENT=true  → this script creates the Fly app + secrets
#   2. GitHub repo variable DEPLOY_CONTENT=true  → deploy.yml adds it to the matrix
# Setting only one creates a mismatch (app missing on deploy, or secrets unset).
DEPLOY_CONTENT="${DEPLOY_CONTENT:-false}"

# Cross-check the GitHub repo variable when DEPLOY_CONTENT is on locally,
# so silent mismatches don't surface as "content service never deploys".
# Soft-fails when gh CLI is missing/unauthenticated (the dev may not have
# it set up yet) — only prints a warning then.
if [[ "$DEPLOY_CONTENT" == "true" ]]; then
  if command -v gh >/dev/null 2>&1; then
    gh_value=$(gh variable list 2>/dev/null | awk '$1=="DEPLOY_CONTENT"{print $2}' || true)
    if [[ "$gh_value" != "true" ]]; then
      echo "ERROR: .env.local has DEPLOY_CONTENT=true but the GitHub repo variable is '${gh_value:-unset}'." >&2
      echo "       Set it to match so deploy.yml includes content in the matrix:" >&2
      echo "         gh variable set DEPLOY_CONTENT --body true" >&2
      echo "       Or set DEPLOY_CONTENT=false in .env.local to disable content entirely." >&2
      exit 1
    fi
  else
    echo "WARN: gh CLI not installed — cannot verify GitHub repo variable DEPLOY_CONTENT" >&2
    echo "      matches .env.local. Set it manually in repo Settings → Secrets and" >&2
    echo "      variables → Actions → Variables: DEPLOY_CONTENT=true" >&2
  fi
fi

# Auto-generate JWT signing keypair on first run, persist back to .env.local.
# Identity reads Jwt:SigningKeyPem (raw PEM or base64) when Vault is disabled.
# Persisting means tokens stay valid across redeploys.
if [[ -z "${JWT_SIGNING_KEY_PEM:-}" ]]; then
  echo "==> Generating RSA-2048 JWT signing key (first run)"
  command -v openssl >/dev/null || {
    echo "openssl not found — install it or pre-fill JWT_SIGNING_KEY_PEM" >&2
    exit 1
  }
  pem="$(openssl genrsa 2048 2>/dev/null)"
  pem_b64="$(printf '%s' "$pem" | base64 | tr -d '\n')"
  # Append to .env.local in place. macOS/BSD sed needs the empty arg; GNU sed doesn't.
  if grep -qE '^JWT_SIGNING_KEY_PEM=' "$ENV_FILE"; then
    if [[ "$(uname)" == "Darwin" ]]; then
      sed -i '' "s|^JWT_SIGNING_KEY_PEM=.*|JWT_SIGNING_KEY_PEM=$pem_b64|" "$ENV_FILE"
    else
      sed -i "s|^JWT_SIGNING_KEY_PEM=.*|JWT_SIGNING_KEY_PEM=$pem_b64|" "$ENV_FILE"
    fi
  else
    printf '\nJWT_SIGNING_KEY_PEM=%s\n' "$pem_b64" >> "$ENV_FILE"
  fi
  JWT_SIGNING_KEY_PEM="$pem_b64"
  echo "    written to $ENV_FILE (gitignored)"
fi

# Vault prod-mode key handling.
#
# Vault is now prod-mode (raft on a Fly volume) — the vault container's
# entrypoint self-initializes on first deploy and persists the unseal
# key + root token to /vault/data/.init.json. This script's job is to
# capture them via flyctl ssh, persist to .env.local, and stage them as
# Fly secrets so future restarts auto-unseal without operator action.
#
# The role_id and secret_id for haworks-identity are also fetched from
# the live vault on every run and re-staged on identity. With persistent
# state they're stable, so this is mostly a no-op on subsequent runs;
# the sync still runs to handle volume-recreation scenarios.
#
# The fetch is skipped silently if the vault app isn't deployed yet;
# operators bootstrap → deploy vault → re-run bootstrap.
upsert_env_var() {
  local key="$1"; local value="$2"
  if grep -qE "^${key}=" "$ENV_FILE"; then
    if [[ "$(uname)" == "Darwin" ]]; then
      sed -i '' "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
    else
      sed -i "s|^${key}=.*|${key}=${value}|" "$ENV_FILE"
    fi
  else
    printf '\n%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

VAULT_UNSEAL_KEY="${VAULT_UNSEAL_KEY:-}"
VAULT_ROOT_TOKEN_PROD="${VAULT_ROOT_TOKEN_PROD:-}"
VAULT_HAWORKS_IDENTITY_ROLE_ID="${VAULT_HAWORKS_IDENTITY_ROLE_ID:-}"
VAULT_HAWORKS_IDENTITY_SECRET_ID="${VAULT_HAWORKS_IDENTITY_SECRET_ID:-}"

# Auto-generate Meilisearch master key on first run (32 bytes urandom, base64).
if [[ -z "${MEILI_MASTER_KEY:-}" ]]; then
  echo "==> Generating Meilisearch master key (first run)"
  key="$(head -c 32 /dev/urandom | base64 | tr -d '\n')"
  if grep -qE '^MEILI_MASTER_KEY=' "$ENV_FILE"; then
    if [[ "$(uname)" == "Darwin" ]]; then
      sed -i '' "s|^MEILI_MASTER_KEY=.*|MEILI_MASTER_KEY=$key|" "$ENV_FILE"
    else
      sed -i "s|^MEILI_MASTER_KEY=.*|MEILI_MASTER_KEY=$key|" "$ENV_FILE"
    fi
  else
    printf '\nMEILI_MASTER_KEY=%s\n' "$key" >> "$ENV_FILE"
  fi
  MEILI_MASTER_KEY="$key"
  echo "    written to $ENV_FILE (gitignored)"
fi

# Auto-generate the vault-pg sandbox Postgres password on first run
# (32-char alphanumeric — kept simple to avoid URL-encoding pain in the
# connection string staged on the vault app). This DB is exclusively
# used by the live vault server's database secrets engine for the
# portfolio rotation demo; vault needs admin rights, hence a sandboxed
# Postgres rather than the shared Neon prod DB.
if [[ -z "${VAULT_PG_PASSWORD:-}" ]]; then
  echo "==> Generating vault-pg Postgres password (first run)"
  # openssl rand avoids the SIGPIPE-with-pipefail issue that bites
  # `tr -dc … </dev/urandom | head -c 32` (tr gets SIGPIPE, pipefail
  # treats the whole script as failed). 16 bytes = 32 hex chars,
  # plenty of entropy for a sandboxed demo Postgres.
  VAULT_PG_PASSWORD="$(openssl rand -hex 16)"
  upsert_env_var VAULT_PG_PASSWORD "$VAULT_PG_PASSWORD"
  echo "    written to $ENV_FILE (gitignored)"
fi

PUBLIC_APP="ritualworks-bffweb"
VAULT_APP="ritualworks-vault"
VAULT_PG_APP="ritualworks-vault-pg"
INTERNAL_APPS=(
  ritualworks-identity
  ritualworks-catalog
  ritualworks-orders
  ritualworks-payments
  ritualworks-checkout
  ritualworks-search
  ritualworks-meilisearch
)
if [[ "$DEPLOY_CONTENT" == "true" ]]; then
  INTERNAL_APPS+=(ritualworks-content)
fi
# Vault + vault-pg are created here but kept out of INTERNAL_APPS because
# they don't take the standard common secrets (no RabbitMQ/Redis, and
# their Postgres wiring is bespoke); secrets staged separately below.
ALL_APPS=("$PUBLIC_APP" "$VAULT_APP" "$VAULT_PG_APP" "${INTERNAL_APPS[@]}")

echo "==> Creating Fly apps (skip if exists)"
for app in "${ALL_APPS[@]}"; do
  if flyctl status -a "$app" >/dev/null 2>&1; then
    echo "    $app exists"
  else
    flyctl apps create "$app"
  fi
done

echo "==> Allocating public IPs (BFF only)"
if ! flyctl ips list -a "$PUBLIC_APP" 2>/dev/null | grep -qE 'v4|v6'; then
  flyctl ips allocate-v4 --shared -a "$PUBLIC_APP"
  flyctl ips allocate-v6 -a "$PUBLIC_APP"
else
  echo "    $PUBLIC_APP already has IPs"
fi

set_secrets() {
  local app="$1"; shift
  # `flyctl secrets set` takes K=V pairs as positional args — bulletproof
  # for any value. Avoid `flyctl secrets import` (stdin pipe) which mangles
  # values containing `&` and similar special characters when parsed
  # line-by-line in some flyctl versions.
  flyctl secrets set --stage -a "$app" "$@" >/dev/null
  echo "    staged $# secrets for $app"
}

# Common: every service talks to RabbitMQ + Redis, has Vault enabled +
# pointing at the internal vault address. Per-service AppRole creds
# (Vault__RoleId / wrapped Vault__SecretId / Vault__SecretIdIsWrapped)
# are staged separately by deploy/fly/ci-stage-vault-creds.sh on every
# deploy — bootstrap.sh just wires the static config so every service
# knows where vault lives and is opted in. Until the first
# ci-stage-vault-creds.sh run lands, services without RoleId/SecretId
# staged will fall through to the Vault:Enabled=false code path locally
# (the config provider treats missing creds as not-enabled).
#
# Plus JWKS config — every backend service validates JWTs against identity-svc's
# /.well-known/jwks.json. Internal-only addressing because backends never see
# external traffic; the JwksUri resolves over Fly's 6PN. Issuer/Audience can be
# overridden via .env.local for cross-environment compatibility but default to
# the identity-svc URL pattern matching the JwtBearer setup in identity itself.
common=(
  "ConnectionStrings__rabbitmq=$RABBITMQ_URL"
  "ConnectionStrings__redis=$REDIS_URL"
  "Vault__Enabled=true"
  "Vault__Address=http://ritualworks-vault.internal:8200"
  "Vault__RequireHmacValidation=false"
  "Authentication__Jwks__JwksUri=http://ritualworks-identity.internal:8080/.well-known/jwks.json"
  "Authentication__Jwks__Issuer=${JWT_ISSUER:-https://ritualworks-identity.fly.dev}"
  "Authentication__Jwks__Audience=${JWT_AUDIENCE:-ritualworks-bffweb}"
)

# Parse POSTGRES_BASE (postgres://USER:PASS@HOST) into components for
# ADO.NET-form connection string assembly. Npgsql 9 chokes on Neon's
# URL-form connection strings (they include params like
# `channel_binding=require` that trigger a KeyNotFoundException when
# parsed as keyword/value pairs); ADO.NET form is the native shape and
# parses cleanly. POSTGRES_QUERY is ignored — sslmode is set explicitly
# below as the only param Neon needs from us.
pg_rest="${POSTGRES_BASE#postgres://}"
PG_USER="${pg_rest%%:*}"
pg_rest="${pg_rest#*:}"
PG_PASS="${pg_rest%%@*}"
PG_HOST="${pg_rest##*@}"

# Service name == database name on Neon. Bash 3.2 (default macOS) doesn't
# support associative arrays, so use the simple convention directly.
echo "==> Per-service secrets"
jwt_secrets=(
  "Jwt__SigningKeyPem=$JWT_SIGNING_KEY_PEM"
  "Jwt__KeyId=${JWT_KEY_ID:-fly-1}"
)
[[ -n "${JWT_ISSUER:-}"   ]] && jwt_secrets+=("Jwt__Issuer=$JWT_ISSUER")
[[ -n "${JWT_AUDIENCE:-}" ]] && jwt_secrets+=("Jwt__Audience=$JWT_AUDIENCE")

for app in "${INTERNAL_APPS[@]}"; do
  if [[ "$app" == "ritualworks-meilisearch" ]]; then
    continue
  fi
  db="${app#ritualworks-}"
  conn="Host=${PG_HOST};Port=5432;Database=${db};Username=${PG_USER};Password=${PG_PASS};SslMode=Require;Trust Server Certificate=true"
  set_secrets "$app" "${common[@]}" "${jwt_secrets[@]}" "ConnectionStrings__${db}=$conn"
done

# Meilisearch volume + master key secrets.
echo "==> Meilisearch setup"
if ! flyctl volumes list -a ritualworks-meilisearch 2>/dev/null | grep -q "meili_data"; then
  echo "    creating meili_data volume"
  flyctl volumes create meili_data --size 1 --region "$REGION" -a ritualworks-meilisearch --yes
else
  echo "    meili_data volume exists"
fi

set_secrets ritualworks-meilisearch "MEILI_MASTER_KEY=$MEILI_MASTER_KEY"
set_secrets ritualworks-search "Meilisearch__MasterKey=$MEILI_MASTER_KEY"

# BFF: only secrets here. Service-discovery overrides for the BFF's
# HttpClients (Services__<svc>__http__0=...flycast:8080) live in
# fly.bffweb.toml's [env] block — Fly's secrets API rejects hyphens in
# names, and these aren't secrets anyway (just internal flycast hostnames).
bff_extra=()

# CORS origins for a custom-domain UI. The BFF has sensible defaults
# baked in (localhost dev + canonical pages.dev URLs); only set this
# when a custom domain is in play.
if [[ -n "${PORTFOLIO_SITE_URL:-}" ]]; then
  bff_extra+=(
    "Cors__AllowedOrigins__0=http://localhost:4321"
    "Cors__AllowedOrigins__1=https://ritualworks.pages.dev"
    "Cors__AllowedOrigins__2=https://portfolio-showcase.pages.dev"
    "Cors__AllowedOrigins__3=$PORTFOLIO_SITE_URL"
  )
fi

if [[ ${#bff_extra[@]} -gt 0 ]]; then
  set_secrets "$PUBLIC_APP" "${common[@]}" "${bff_extra[@]}"
else
  set_secrets "$PUBLIC_APP" "${common[@]}"
fi

echo "==> Vault Postgres setup (sandboxed DB for vault's database secrets engine)"

# Sandboxed Postgres exclusively for the live vault server's database
# secrets engine (rotation demo). Vault needs admin rights to create
# and revoke ephemeral roles, so we cannot point it at the shared Neon
# prod DB used by the real services.
if ! flyctl volumes list -a "$VAULT_PG_APP" 2>/dev/null | grep -q "vault_pg_data"; then
  echo "    creating vault_pg_data volume"
  flyctl volumes create vault_pg_data --size 1 --region "$REGION" -a "$VAULT_PG_APP" --yes
else
  echo "    vault_pg_data volume exists"
fi

# postgres-flex reads OPERATOR_PASSWORD (and POSTGRES_PASSWORD as a
# fallback alias used by some upstream tooling) on first boot to set the
# `postgres` superuser password. Re-staging on subsequent runs is a
# no-op for the running DB but keeps Fly's secret store in sync with
# .env.local.
set_secrets "$VAULT_PG_APP" \
  "OPERATOR_PASSWORD=$VAULT_PG_PASSWORD" \
  "POSTGRES_PASSWORD=$VAULT_PG_PASSWORD"

# Stage the connection URL on the vault app so deploy/vault/seed.sh
# (different agent owns it) can wire up the database secrets engine
# without re-deriving credentials.
set_secrets "$VAULT_APP" \
  "VAULT_PG_CONNECTION_URL=postgres://postgres:$VAULT_PG_PASSWORD@ritualworks-vault-pg.internal:5432/postgres?sslmode=disable"

echo "==> Vault setup"

# Ensure the persistent volume exists. Vault prod-mode stores raft data
# (initialized state, AppRole config, KV) here; without it every deploy
# would wipe everything.
if ! flyctl volumes list -a "$VAULT_APP" 2>/dev/null | grep -q "vault_data"; then
  echo "    creating vault_data volume"
  flyctl volumes create vault_data --size 1 --region "$REGION" -a "$VAULT_APP" --yes
else
  echo "    vault_data volume exists"
fi

# Capture init keys + AppRole creds from the live vault and persist
# them. This is a no-op on subsequent runs — the values from .env.local
# match what's already staged. First-ever run pulls the keys vault
# generated at init time on the persistent volume.
if flyctl status -a "$VAULT_APP" 2>/dev/null | grep -qE 'started\s'; then
  # The vault entrypoint writes /vault/data/.init.json on first init.
  # Read it once, persist locally, stage as Fly secrets so subsequent
  # restarts auto-unseal from VAULT_UNSEAL_KEY env (no .init.json
  # dependency).
  if [[ -z "$VAULT_UNSEAL_KEY" || -z "$VAULT_ROOT_TOKEN_PROD" ]]; then
    echo "    capturing unseal key + root token from $VAULT_APP"
    # `|| true` keeps `set -e` from killing the script when /vault/data
    # /.init.json is missing (the file only exists after the new prod-mode
    # entrypoint has run its first init).
    init_json=$(flyctl ssh console -a "$VAULT_APP" -C 'cat /vault/data/.init.json' 2>/dev/null \
      | tr -d '\r' | sed -n '/^Connecting/!p' | sed -n '/{/,$p' || true)
    if [[ -n "$init_json" ]]; then
      VAULT_UNSEAL_KEY=$(echo "$init_json" | jq -r '.unseal_keys_b64[0]' 2>/dev/null)
      VAULT_ROOT_TOKEN_PROD=$(echo "$init_json" | jq -r '.root_token' 2>/dev/null)
      if [[ -n "$VAULT_UNSEAL_KEY" && "$VAULT_UNSEAL_KEY" != "null" ]]; then
        upsert_env_var VAULT_UNSEAL_KEY "$VAULT_UNSEAL_KEY"
        upsert_env_var VAULT_ROOT_TOKEN_PROD "$VAULT_ROOT_TOKEN_PROD"
        echo "    ✓ captured init keys → $ENV_FILE"
      fi
    fi
  fi

  # Once vault is unsealed (entrypoint handles that), grab the AppRole
  # role_id + a fresh secret_id and stage them on identity. These are
  # stable across vault restarts because raft persists them.
  if [[ -n "$VAULT_ROOT_TOKEN_PROD" ]]; then
    echo "    fetching AppRole role_id/secret_id"
    read_role='vault read -field=role_id auth/approle/role/haworks-identity-app/role-id'
    write_secret='vault write -force -field=secret_id auth/approle/role/haworks-identity-app/secret-id'
    cmd="export VAULT_ADDR=http://[::1]:8200 VAULT_TOKEN=$VAULT_ROOT_TOKEN_PROD; $read_role"
    VAULT_HAWORKS_IDENTITY_ROLE_ID=$(flyctl ssh console -a "$VAULT_APP" -C "sh -c '$cmd'" 2>/dev/null \
      | tr -d '\r' | tail -1 | tr -d ' ' || true)
    cmd="export VAULT_ADDR=http://[::1]:8200 VAULT_TOKEN=$VAULT_ROOT_TOKEN_PROD; $write_secret"
    VAULT_HAWORKS_IDENTITY_SECRET_ID=$(flyctl ssh console -a "$VAULT_APP" -C "sh -c '$cmd'" 2>/dev/null \
      | tr -d '\r' | tail -1 | tr -d ' ' || true)
    if echo "$VAULT_HAWORKS_IDENTITY_ROLE_ID" | grep -qE '^[0-9a-f-]{36}$' \
       && echo "$VAULT_HAWORKS_IDENTITY_SECRET_ID" | grep -qE '^[0-9a-f-]{36}$'; then
      upsert_env_var VAULT_HAWORKS_IDENTITY_ROLE_ID "$VAULT_HAWORKS_IDENTITY_ROLE_ID"
      upsert_env_var VAULT_HAWORKS_IDENTITY_SECRET_ID "$VAULT_HAWORKS_IDENTITY_SECRET_ID"
      echo "    ✓ AppRole creds (role_id=${VAULT_HAWORKS_IDENTITY_ROLE_ID:0:8}..., secret_id=${VAULT_HAWORKS_IDENTITY_SECRET_ID:0:8}...)"
    else
      echo "    ! AppRole fetch returned unexpected output — skipping identity stage"
      VAULT_HAWORKS_IDENTITY_ROLE_ID=""
      VAULT_HAWORKS_IDENTITY_SECRET_ID=""
    fi
  fi
else
  echo "    $VAULT_APP not yet deployed — re-run bootstrap.sh after first vault deploy"
fi

# Stage the unseal key + root token on the vault app so future restarts
# auto-unseal from env. Skipped on first-ever bootstrap (before the
# init has run); the operator re-runs bootstrap.sh after first deploy.
vault_secrets=()
[[ -n "$VAULT_UNSEAL_KEY"       ]] && vault_secrets+=("VAULT_UNSEAL_KEY=$VAULT_UNSEAL_KEY")
[[ -n "$VAULT_ROOT_TOKEN_PROD"  ]] && vault_secrets+=("VAULT_ROOT_TOKEN_PROD=$VAULT_ROOT_TOKEN_PROD")
[[ ${#vault_secrets[@]} -gt 0 ]] && set_secrets "$VAULT_APP" "${vault_secrets[@]}"

# Identity-specific: JWT key + Vault wiring (direct creds from staged
# Fly secrets — no boot-time round-trip to vault) + optional issuer +
# optional OAuth. Vault__Enabled=true overrides the common false set
# earlier (later set_secrets wins).
id_extra=(
  "Jwt__SigningKeyPem=$JWT_SIGNING_KEY_PEM"
  "Jwt__KeyId=${JWT_KEY_ID:-fly-1}"
  "Vault__Enabled=true"
  "Vault__Address=http://ritualworks-vault.internal:8200"
  "Vault__RequireHmacValidation=false"
  "Vault__CaCertPath="
)
if [[ -n "$VAULT_HAWORKS_IDENTITY_ROLE_ID" && -n "$VAULT_HAWORKS_IDENTITY_SECRET_ID" ]]; then
  id_extra+=(
    "Vault__RoleId=$VAULT_HAWORKS_IDENTITY_ROLE_ID"
    "Vault__SecretId=$VAULT_HAWORKS_IDENTITY_SECRET_ID"
  )
fi
[[ -n "${JWT_ISSUER:-}"   ]] && id_extra+=("Jwt__Issuer=$JWT_ISSUER")
[[ -n "${JWT_AUDIENCE:-}" ]] && id_extra+=("Jwt__Audience=$JWT_AUDIENCE")
[[ -n "${OAUTH_GOOGLE_CLIENT_ID:-}" ]] && id_extra+=(
  "Authentication__Google__ClientId=$OAUTH_GOOGLE_CLIENT_ID"
  "Authentication__Google__ClientSecret=$OAUTH_GOOGLE_CLIENT_SECRET"
)
[[ -n "${OAUTH_MICROSOFT_CLIENT_ID:-}" ]] && id_extra+=(
  "Authentication__Microsoft__ClientId=$OAUTH_MICROSOFT_CLIENT_ID"
  "Authentication__Microsoft__ClientSecret=$OAUTH_MICROSOFT_CLIENT_SECRET"
)
[[ -n "${OAUTH_FACEBOOK_APP_ID:-}" ]] && id_extra+=(
  "Authentication__Facebook__AppId=$OAUTH_FACEBOOK_APP_ID"
  "Authentication__Facebook__AppSecret=$OAUTH_FACEBOOK_APP_SECRET"
)
set_secrets ritualworks-identity "${id_extra[@]}"

# Payments-specific
[[ -n "${STRIPE_WEBHOOK_SECRET:-}" ]] && \
  set_secrets ritualworks-payments "Webhooks__Stripe__WebhookSecret=$STRIPE_WEBHOOK_SECRET"

# Content-specific (only when DEPLOY_CONTENT=true).
# Storage backend on Fly is Tigris (S3-compatible). Same Storage__* env shape
# as local-dev/test (LocalStack) — only ServiceUrl/Region/ForcePathStyle vary.
# Tigris credentials come from `flyctl storage create -a ritualworks-content`
# (printed once as AWS_*); stash them in .env.local under TIGRIS_* slots.
if [[ "$DEPLOY_CONTENT" == "true" ]]; then
  content_extra=()
  [[ -n "${TIGRIS_ACCESS_KEY:-}" ]] && content_extra+=("Storage__AccessKey=$TIGRIS_ACCESS_KEY")
  [[ -n "${TIGRIS_SECRET_KEY:-}" ]] && content_extra+=("Storage__SecretKey=$TIGRIS_SECRET_KEY")
  [[ -n "${TIGRIS_BUCKET:-}"     ]] && content_extra+=("Storage__BucketName=$TIGRIS_BUCKET")
  content_extra+=(
    "Storage__ServiceUrl=${TIGRIS_SERVICE_URL:-https://fly.storage.tigris.dev}"
    "Storage__Region=${TIGRIS_REGION:-auto}"
    "Storage__ForcePathStyle=false"
  )
  [[ -n "${CLAMAV_REST_URL:-}" ]] && content_extra+=("ClamAV__RestApiUrl=$CLAMAV_REST_URL")
  [[ ${#content_extra[@]} -gt 0 ]] && set_secrets ritualworks-content "${content_extra[@]}"
fi

echo
echo "Secrets staged. They take effect on the next deploy."
echo "Run: deploy/fly/deploy.sh"
