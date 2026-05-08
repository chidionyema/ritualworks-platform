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

# Auto-generate Vault dev-mode root token on first run. Same value is staged
# on the vault app (as VAULT_DEV_ROOT_TOKEN_ID — what `vault server -dev`
# expects) and on identity (as VAULT_ROOT_TOKEN — what the bootstrap shim
# reads). Persisting to .env.local means restaging is idempotent across runs.
if [[ -z "${VAULT_DEV_ROOT_TOKEN:-}" ]]; then
  echo "==> Generating Vault dev root token (first run)"
  token="rw-dev-$(head -c 24 /dev/urandom | base64 | tr -d '\n=+/' | head -c 32)"
  if grep -qE '^VAULT_DEV_ROOT_TOKEN=' "$ENV_FILE"; then
    if [[ "$(uname)" == "Darwin" ]]; then
      sed -i '' "s|^VAULT_DEV_ROOT_TOKEN=.*|VAULT_DEV_ROOT_TOKEN=$token|" "$ENV_FILE"
    else
      sed -i "s|^VAULT_DEV_ROOT_TOKEN=.*|VAULT_DEV_ROOT_TOKEN=$token|" "$ENV_FILE"
    fi
  else
    printf '\nVAULT_DEV_ROOT_TOKEN=%s\n' "$token" >> "$ENV_FILE"
  fi
  VAULT_DEV_ROOT_TOKEN="$token"
  echo "    written to $ENV_FILE (gitignored)"
fi

# Deterministic AppRole creds for ritualworks-identity. Generated once on
# first run, persisted to .env.local, supplied to vault's seed.sh via Fly
# secrets so seed.sh registers them as the role's custom-role-id and
# custom-secret-id. Same values stay valid across every vault redeploy
# (which wipes dev-mode's in-memory state) — operators don't have to
# re-stage identity's Vault__RoleId/SecretId after each vault deploy.
gen_uuid() {
  if command -v uuidgen >/dev/null 2>&1; then
    uuidgen | tr '[:upper:]' '[:lower:]'
  else
    # POSIX fallback: 16 random bytes, hex-formatted as a UUID4.
    head -c 16 /dev/urandom | xxd -p \
      | sed -E 's/(........)(....)(....)(....)(............)/\1-\2-\3-\4-\5/'
  fi
}
if [[ -z "${VAULT_HAWORKS_IDENTITY_ROLE_ID:-}" ]]; then
  echo "==> Generating deterministic AppRole creds for haworks-identity (first run)"
  VAULT_HAWORKS_IDENTITY_ROLE_ID="$(gen_uuid)"
  VAULT_HAWORKS_IDENTITY_SECRET_ID="$(gen_uuid)"
  if grep -qE '^VAULT_HAWORKS_IDENTITY_ROLE_ID=' "$ENV_FILE"; then
    if [[ "$(uname)" == "Darwin" ]]; then
      sed -i '' "s|^VAULT_HAWORKS_IDENTITY_ROLE_ID=.*|VAULT_HAWORKS_IDENTITY_ROLE_ID=$VAULT_HAWORKS_IDENTITY_ROLE_ID|" "$ENV_FILE"
      sed -i '' "s|^VAULT_HAWORKS_IDENTITY_SECRET_ID=.*|VAULT_HAWORKS_IDENTITY_SECRET_ID=$VAULT_HAWORKS_IDENTITY_SECRET_ID|" "$ENV_FILE"
    else
      sed -i "s|^VAULT_HAWORKS_IDENTITY_ROLE_ID=.*|VAULT_HAWORKS_IDENTITY_ROLE_ID=$VAULT_HAWORKS_IDENTITY_ROLE_ID|" "$ENV_FILE"
      sed -i "s|^VAULT_HAWORKS_IDENTITY_SECRET_ID=.*|VAULT_HAWORKS_IDENTITY_SECRET_ID=$VAULT_HAWORKS_IDENTITY_SECRET_ID|" "$ENV_FILE"
    fi
  else
    printf '\nVAULT_HAWORKS_IDENTITY_ROLE_ID=%s\nVAULT_HAWORKS_IDENTITY_SECRET_ID=%s\n' \
      "$VAULT_HAWORKS_IDENTITY_ROLE_ID" "$VAULT_HAWORKS_IDENTITY_SECRET_ID" >> "$ENV_FILE"
  fi
  echo "    written to $ENV_FILE (gitignored)"
fi

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

PUBLIC_APP="ritualworks-bffweb"
VAULT_APP="ritualworks-vault"
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
# Vault is created here but kept out of INTERNAL_APPS because it doesn't take
# the standard common secrets (no Postgres/RabbitMQ/Redis); secrets staged
# separately below.
ALL_APPS=("$PUBLIC_APP" "$VAULT_APP" "${INTERNAL_APPS[@]}")

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

# Common: every service talks to RabbitMQ + Redis, has Vault disabled on Fly.
common=(
  "ConnectionStrings__rabbitmq=$RABBITMQ_URL"
  "ConnectionStrings__redis=$REDIS_URL"
  "Vault__Enabled=false"
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
for app in "${INTERNAL_APPS[@]}"; do
  if [[ "$app" == "ritualworks-meilisearch" ]]; then
    continue
  fi
  db="${app#ritualworks-}"
  conn="Host=${PG_HOST};Port=5432;Database=${db};Username=${PG_USER};Password=${PG_PASS};SslMode=Require;Trust Server Certificate=true"
  set_secrets "$app" "${common[@]}" "ConnectionStrings__${db}=$conn"
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

# Vault dev-mode app: only secret is the dev root token, used by both the
# `vault server -dev` flag and (mirrored as VAULT_ROOT_TOKEN) the identity
# bootstrap shim. Vault is reachable at http://ritualworks-vault.internal:8200
# from any other ritualworks-* machine inside Fly's 6PN.
echo "==> Vault setup"
# Vault gets the dev root token + the deterministic AppRole creds passed
# as env vars; seed.sh applies them as the role's custom-role-id/
# custom-secret-id on every container start. This way every vault
# redeploy (which wipes dev-mode's in-memory state) ends up with the
# same creds that identity already has staged.
set_secrets "$VAULT_APP" \
  "VAULT_DEV_ROOT_TOKEN_ID=$VAULT_DEV_ROOT_TOKEN" \
  "VAULT_HAWORKS_IDENTITY_ROLE_ID=$VAULT_HAWORKS_IDENTITY_ROLE_ID" \
  "VAULT_HAWORKS_IDENTITY_SECRET_ID=$VAULT_HAWORKS_IDENTITY_SECRET_ID"

# Identity-specific: JWT key + Vault wiring (direct creds — no boot-time
# round-trip to vault) + optional issuer/audience + optional OAuth.
# Vault__Enabled=true here overrides the common Vault__Enabled=false above
# (later set_secrets call wins).
id_extra=(
  "Jwt__SigningKeyPem=$JWT_SIGNING_KEY_PEM"
  "Jwt__KeyId=${JWT_KEY_ID:-fly-1}"
  "Vault__Enabled=true"
  "Vault__Address=http://ritualworks-vault.internal:8200"
  "Vault__RequireHmacValidation=false"
  "Vault__CaCertPath="
  # Direct mode: VaultConfigBootstrap reads Vault:RoleId/SecretId from
  # config; the bootstrap shim short-circuits when both are present.
  # Identity boots in <5s with zero startup dependency on vault.
  "Vault__RoleId=$VAULT_HAWORKS_IDENTITY_ROLE_ID"
  "Vault__SecretId=$VAULT_HAWORKS_IDENTITY_SECRET_ID"
)
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

# Content-specific (only when DEPLOY_CONTENT=true)
if [[ "$DEPLOY_CONTENT" == "true" ]]; then
  content_extra=()
  [[ -n "${MINIO_ENDPOINT:-}" ]] && content_extra+=(
    "MinIO__Endpoint=$MINIO_ENDPOINT"
    "MinIO__AccessKey=$MINIO_ACCESS_KEY"
    "MinIO__SecretKey=$MINIO_SECRET_KEY"
    "MinIO__BucketName=$MINIO_BUCKET"
    "MinIO__Secure=${MINIO_SECURE:-true}"
  )
  [[ -n "${CLAMAV_REST_URL:-}" ]] && content_extra+=("ClamAV__RestApiUrl=$CLAMAV_REST_URL")
  [[ ${#content_extra[@]} -gt 0 ]] && set_secrets ritualworks-content "${content_extra[@]}"
fi

echo
echo "Secrets staged. They take effect on the next deploy."
echo "Run: deploy/fly/deploy.sh"
