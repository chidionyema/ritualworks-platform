#!/usr/bin/env bash
set -euo pipefail

# =============================================================================
# Haworks Platform — Fly.io App Migration Script
# Migrates from ritualworks-* to haworks-* apps
#
# Usage:
#   1. Fill in the secret values below (marked with CHANGEME)
#   2. Run: chmod +x scripts/migrate-fly-apps.sh && ./scripts/migrate-fly-apps.sh
#   3. After verifying all haworks-* apps are healthy, run with --destroy-old
# =============================================================================

DESTROY_OLD="${1:-}"

# ---------------------------------------------------------------------------
# STEP 0: Shared secret values — FILL THESE IN
# ---------------------------------------------------------------------------
RABBITMQ_URL="CHANGEME"          # amqp://user:pass@host:5672
REDIS_URL="CHANGEME"             # redis://host:6379
VAULT_ADDRESS="https://haworks-vault.fly.dev"
VAULT_ROLE_ID="CHANGEME"
VAULT_SECRET_ID="CHANGEME"
MT_LICENSE="CHANGEME"            # MassTransit license key
SHARED_SECRET="CHANGEME"         # ServiceAuth shared secret
JWT_SIGNING_KEY_PEM="CHANGEME"   # RSA PEM for JWT signing
JWT_KEY_ID="CHANGEME"
JWT_AUDIENCE="CHANGEME"
JWT_ISSUER="CHANGEME"
JWKS_URI="CHANGEME"              # e.g., https://haworks-identity.fly.dev/.well-known/jwks

# Per-service Neon Postgres connection strings
PG_IDENTITY="CHANGEME"
PG_CATALOG="CHANGEME"
PG_CHECKOUT="CHANGEME"
PG_ORDERS="CHANGEME"
PG_PAYMENTS="CHANGEME"
PG_NOTIFICATIONS="CHANGEME"
PG_SEARCH="CHANGEME"
PG_AUDIT="CHANGEME"

# Vault-specific
VAULT_DEV_ROOT_TOKEN="CHANGEME"
VAULT_ROOT_TOKEN_PROD="CHANGEME"
VAULT_UNSEAL_KEY="CHANGEME"
VAULT_PG_URL="CHANGEME"
VAULT_IDENTITY_ROLE_ID="CHANGEME"
VAULT_IDENTITY_SECRET_ID="CHANGEME"

# Search-specific
ELASTICSEARCH_URL="CHANGEME"
ELASTICSEARCH_API_KEY="CHANGEME"
ELASTICSEARCH_INDEX="CHANGEME"

# Notifications-specific
SENDGRID_API_KEY="CHANGEME"
SENDGRID_FROM="CHANGEME"
TWILIO_SID="CHANGEME"
TWILIO_TOKEN="CHANGEME"
TWILIO_FROM="CHANGEME"
FCM_PROJECT_ID="CHANGEME"
FCM_SERVICE_ACCOUNT="CHANGEME"

# Checkout-specific
CHECKOUT_SUCCESS_URL="https://haworks.com/checkout/success"
CHECKOUT_CANCEL_URL="https://haworks.com/checkout/cancel"

# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------
set_common_secrets() {
  local app="$1"
  echo "  Setting common secrets on $app..."
  fly secrets set -a "$app" \
    ConnectionStrings__rabbitmq="$RABBITMQ_URL" \
    ConnectionStrings__redis="$REDIS_URL" \
    Vault__Enabled=true \
    Vault__Address="$VAULT_ADDRESS" \
    Vault__RoleId="$VAULT_ROLE_ID" \
    Vault__SecretId="$VAULT_SECRET_ID" \
    Vault__SecretIdIsWrapped=true \
    Vault__RequireHmacValidation=true \
    MT_LICENSE="$MT_LICENSE" \
    Authentication__Jwks__Audience="$JWT_AUDIENCE" \
    Authentication__Jwks__Issuer="$JWT_ISSUER" \
    Authentication__Jwks__JwksUri="$JWKS_URI" \
    Jwt__KeyId="$JWT_KEY_ID" \
    Jwt__SigningKeyPem="$JWT_SIGNING_KEY_PEM" \
    Jwt__Audience="$JWT_AUDIENCE" \
    Jwt__Issuer="$JWT_ISSUER" \
    --stage
}

# ---------------------------------------------------------------------------
# STEP 1: Set secrets on all haworks-* apps
# ---------------------------------------------------------------------------
echo "=== STEP 1: Provisioning secrets ==="

# Vault (special — no common secrets)
echo "[1/10] haworks-vault"
fly secrets set -a haworks-vault \
  VAULT_DEV_ROOT_TOKEN_ID="$VAULT_DEV_ROOT_TOKEN" \
  VAULT_ROOT_TOKEN_PROD="$VAULT_ROOT_TOKEN_PROD" \
  VAULT_UNSEAL_KEY="$VAULT_UNSEAL_KEY" \
  VAULT_PG_CONNECTION_URL="$VAULT_PG_URL" \
  VAULT_HAWORKS_IDENTITY_ROLE_ID="$VAULT_IDENTITY_ROLE_ID" \
  VAULT_HAWORKS_IDENTITY_SECRET_ID="$VAULT_IDENTITY_SECRET_ID" \
  --stage

# Identity
echo "[2/10] haworks-identity"
set_common_secrets haworks-identity
fly secrets set -a haworks-identity \
  ConnectionStrings__identity="$PG_IDENTITY" \
  VAULT_ROOT_TOKEN="$VAULT_ROOT_TOKEN_PROD" \
  --stage

# BFF Web
echo "[3/10] haworks-bffweb"
fly secrets set -a haworks-bffweb \
  ConnectionStrings__rabbitmq="$RABBITMQ_URL" \
  ConnectionStrings__redis="$REDIS_URL" \
  Vault__Enabled=true \
  Vault__Address="$VAULT_ADDRESS" \
  Vault__RoleId="$VAULT_ROLE_ID" \
  Vault__SecretId="$VAULT_SECRET_ID" \
  Vault__SecretIdIsWrapped=true \
  Vault__RequireHmacValidation=true \
  MT_LICENSE="$MT_LICENSE" \
  Authentication__Jwks__Audience="$JWT_AUDIENCE" \
  Authentication__Jwks__Issuer="$JWT_ISSUER" \
  Authentication__Jwks__JwksUri="$JWKS_URI" \
  ServiceAuth__SharedSecret="$SHARED_SECRET" \
  ServiceAuth__IdentityUrl="$JWT_ISSUER" \
  Services__Identity__BaseUrl="$JWT_ISSUER" \
  --stage

# Catalog
echo "[4/10] haworks-catalog"
set_common_secrets haworks-catalog
fly secrets set -a haworks-catalog \
  ConnectionStrings__catalog="$PG_CATALOG" \
  --stage

# Checkout
echo "[5/10] haworks-checkout"
set_common_secrets haworks-checkout
fly secrets set -a haworks-checkout \
  ConnectionStrings__checkout="$PG_CHECKOUT" \
  Checkout__SuccessUrl="$CHECKOUT_SUCCESS_URL" \
  Checkout__CancelUrl="$CHECKOUT_CANCEL_URL" \
  --stage

# Orders
echo "[6/10] haworks-orders"
set_common_secrets haworks-orders
fly secrets set -a haworks-orders \
  ConnectionStrings__orders="$PG_ORDERS" \
  --stage

# Payments
echo "[7/10] haworks-payments"
set_common_secrets haworks-payments
fly secrets set -a haworks-payments \
  ConnectionStrings__payments="$PG_PAYMENTS" \
  --stage

# Search
echo "[8/10] haworks-search"
set_common_secrets haworks-search
fly secrets set -a haworks-search \
  ConnectionStrings__search="$PG_SEARCH" \
  Elasticsearch__Url="$ELASTICSEARCH_URL" \
  Elasticsearch__ApiKey="$ELASTICSEARCH_API_KEY" \
  Elasticsearch__IndexName="$ELASTICSEARCH_INDEX" \
  --stage

# Notifications
echo "[9/10] haworks-notifications"
set_common_secrets haworks-notifications
fly secrets set -a haworks-notifications \
  ConnectionStrings__notifications="$PG_NOTIFICATIONS" \
  Notifications__Providers__SendGrid__ApiKey="$SENDGRID_API_KEY" \
  Notifications__Providers__SendGrid__FromAddress="$SENDGRID_FROM" \
  Notifications__Providers__Twilio__AccountSid="$TWILIO_SID" \
  Notifications__Providers__Twilio__AuthToken="$TWILIO_TOKEN" \
  Notifications__Providers__Twilio__FromNumber="$TWILIO_FROM" \
  Notifications__Providers__Fcm__ProjectId="$FCM_PROJECT_ID" \
  Notifications__Providers__Fcm__ServiceAccountJson="$FCM_SERVICE_ACCOUNT" \
  --stage

# Audit
echo "[10/10] haworks-audit"
set_common_secrets haworks-audit
fly secrets set -a haworks-audit \
  ConnectionStrings__audit="$PG_AUDIT" \
  --stage

echo ""
echo "=== STEP 1 COMPLETE: All secrets staged ==="
echo ""

# ---------------------------------------------------------------------------
# STEP 2: Deploy all services (order matters — vault first, then identity)
# ---------------------------------------------------------------------------
echo "=== STEP 2: Deploying services ==="

DEPLOY_ORDER=(
  "fly.vault.toml"
  "fly.identity.toml"
  "fly.catalog.toml"
  "fly.orders.toml"
  "fly.payments.toml"
  "fly.checkout.toml"
  "fly.search.toml"
  "fly.notifications.toml"
  "fly.audit.toml"
  "fly.bffweb.toml"
)

for toml in "${DEPLOY_ORDER[@]}"; do
  svc=$(echo "$toml" | sed 's/fly\.\(.*\)\.toml/\1/')
  echo "Deploying haworks-$svc..."
  flyctl deploy -c "$toml" --remote-only --strategy rolling 2>&1 | tail -3
  echo ""
done

echo "=== STEP 2 COMPLETE: All services deployed ==="
echo ""

# ---------------------------------------------------------------------------
# STEP 3: Health check
# ---------------------------------------------------------------------------
echo "=== STEP 3: Health checks ==="

HEALTH_APPS=(
  "haworks-bffweb"
  "haworks-identity"
  "haworks-catalog"
  "haworks-orders"
  "haworks-payments"
  "haworks-checkout"
  "haworks-search"
  "haworks-vault"
)

all_healthy=true
for app in "${HEALTH_APPS[@]}"; do
  status=$(curl -s -o /dev/null -w "%{http_code}" "https://${app}.fly.dev/health" 2>/dev/null || echo "000")
  if [ "$status" = "200" ]; then
    echo "  ✅ $app — healthy"
  else
    echo "  ❌ $app — status $status"
    all_healthy=false
  fi
done

echo ""
if [ "$all_healthy" = true ]; then
  echo "All services healthy!"
else
  echo "WARNING: Some services unhealthy. Check logs with: fly logs -a <app-name>"
  echo "Do NOT destroy old apps until all new apps are healthy."
  exit 1
fi

# ---------------------------------------------------------------------------
# STEP 4: Destroy old apps (only with --destroy-old flag)
# ---------------------------------------------------------------------------
if [ "$DESTROY_OLD" = "--destroy-old" ]; then
  echo ""
  echo "=== STEP 4: Destroying old ritualworks-* apps ==="

  OLD_APPS=(
    "ritualworks-audit"
    "ritualworks-bffweb"
    "ritualworks-catalog"
    "ritualworks-checkout"
    "ritualworks-content"
    "ritualworks-identity"
    "ritualworks-meilisearch"
    "ritualworks-notifications"
    "ritualworks-orders"
    "ritualworks-payments"
    "ritualworks-platform"
    "ritualworks-search"
    "ritualworks-vault"
    "ritualworks-vault-pg"
    "ritualworks-webhooks"
  )

  for app in "${OLD_APPS[@]}"; do
    echo "  Destroying $app..."
    fly apps destroy "$app" --yes 2>&1 || echo "  (already destroyed or not found)"
  done

  echo ""
  echo "=== STEP 4 COMPLETE: Old apps destroyed ==="
fi

echo ""
echo "=== MIGRATION COMPLETE ==="
echo "Post-deploy scan target: https://haworks-bffweb.fly.dev"
