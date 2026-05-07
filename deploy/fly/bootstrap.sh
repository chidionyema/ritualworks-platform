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

PUBLIC_APP="ritualworks-bffweb"
INTERNAL_APPS=(
  ritualworks-identity
  ritualworks-catalog
  ritualworks-orders
  ritualworks-payments
  ritualworks-checkout
)
if [[ "$DEPLOY_CONTENT" == "true" ]]; then
  INTERNAL_APPS+=(ritualworks-content)
fi
ALL_APPS=("$PUBLIC_APP" "${INTERNAL_APPS[@]}")

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
  printf '%s\n' "$@" | flyctl secrets import --stage -a "$app" >/dev/null
  echo "    staged $# secrets for $app"
}

# Common: every service talks to RabbitMQ + Redis, has Vault disabled on Fly.
common=(
  "ConnectionStrings__rabbitmq=$RABBITMQ_URL"
  "ConnectionStrings__redis=$REDIS_URL"
  "Vault__Enabled=false"
)

declare -A DB_NAME=(
  [ritualworks-identity]=identity
  [ritualworks-catalog]=catalog
  [ritualworks-orders]=orders
  [ritualworks-payments]=payments
  [ritualworks-checkout]=checkout
  [ritualworks-content]=content
)
declare -A DB_KEY=(
  [ritualworks-identity]=ConnectionStrings__identity
  [ritualworks-catalog]=ConnectionStrings__catalog
  [ritualworks-orders]=ConnectionStrings__orders
  [ritualworks-payments]=ConnectionStrings__payments
  [ritualworks-checkout]=ConnectionStrings__checkout
  [ritualworks-content]=ConnectionStrings__content
)

echo "==> Per-service secrets"
for app in "${INTERNAL_APPS[@]}"; do
  conn="${POSTGRES_BASE}/${DB_NAME[$app]}${POSTGRES_QUERY}"
  set_secrets "$app" "${common[@]}" "${DB_KEY[$app]}=$conn"
done

# BFF: no DB, but needs service-discovery overrides for HttpClients.
# Keys must match BackendClients.cs constants exactly.
bff_extra=(
  "Services__identity-svc__http__0=http://ritualworks-identity.flycast:8080"
  "Services__catalog-svc__http__0=http://ritualworks-catalog.flycast:8080"
  "Services__orders-svc__http__0=http://ritualworks-orders.flycast:8080"
  "Services__payments-svc__http__0=http://ritualworks-payments.flycast:8080"
  "Services__checkout-svc__http__0=http://ritualworks-checkout.flycast:8080"
)
# CORS origins for the Cloudflare-Pages-hosted UI. The BFF has sensible
# defaults baked in (localhost dev + canonical pages.dev URLs); only set
# this when a custom domain is in play.
if [[ -n "${PORTFOLIO_SITE_URL:-}" ]]; then
  bff_extra+=(
    "Cors__AllowedOrigins__0=http://localhost:4321"
    "Cors__AllowedOrigins__1=https://ritualworks.pages.dev"
    "Cors__AllowedOrigins__2=https://portfolio-showcase.pages.dev"
    "Cors__AllowedOrigins__3=$PORTFOLIO_SITE_URL"
  )
fi
set_secrets "$PUBLIC_APP" "${common[@]}" "${bff_extra[@]}"

# Identity-specific: JWT key + optional issuer/audience + optional OAuth.
id_extra=(
  "Jwt__SigningKeyPem=$JWT_SIGNING_KEY_PEM"
  "Jwt__KeyId=${JWT_KEY_ID:-fly-1}"
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
