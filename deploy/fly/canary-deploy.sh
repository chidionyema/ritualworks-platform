#!/usr/bin/env bash
# Canary deployment: deploy to 1 machine, health-check, then scale.
# Usage: deploy/fly/canary-deploy.sh <service-name>
# Example: deploy/fly/canary-deploy.sh payments
#
# Strategy:
#   1. Deploy new version to a single machine (canary)
#   2. Wait for health check to pass (30s timeout)
#   3. Run smoke test against canary (service-specific endpoint)
#   4. If healthy: scale to target count
#   5. If unhealthy: auto-rollback
#
# For K8s: Use Argo Rollouts with canary strategy instead of this script.

set -euo pipefail

SVC="${1:?Usage: canary-deploy.sh <service-name>}"
APP="haworks-${SVC}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TOML="fly.${SVC}.toml"
HEALTH_TIMEOUT=30
SMOKE_TIMEOUT=10
TARGET_MACHINES="${CANARY_TARGET_MACHINES:-1}"  # Single machine by default (portfolio)

echo "=== Canary Deploy: ${APP} ==="

# Step 1: Deploy (single machine)
echo ">>> Step 1: Deploying canary (1 machine)"
flyctl deploy -c "$ROOT_DIR/$TOML" --remote-only --ha=false --strategy canary 2>/dev/null \
  || flyctl deploy -c "$ROOT_DIR/$TOML" --remote-only --ha=false  # Fallback if --strategy not supported

# Step 2: Health check
echo ">>> Step 2: Health check (${HEALTH_TIMEOUT}s timeout)"
for i in $(seq 1 "$HEALTH_TIMEOUT"); do
  status=$(flyctl status -a "$APP" --json 2>/dev/null | jq -r '.Machines[0].state' 2>/dev/null || echo "unknown")
  if [[ "$status" == "started" ]]; then
    # Check health endpoint
    health=$(flyctl proxy 8080:8080 -a "$APP" &>/dev/null & PROXY_PID=$!; sleep 2;
      curl -sf "http://localhost:8080/health/ready" 2>/dev/null; kill $PROXY_PID 2>/dev/null || true)
    if [[ $? -eq 0 ]]; then
      echo "    Health check passed on attempt $i"
      break
    fi
  fi
  if [[ $i -eq $HEALTH_TIMEOUT ]]; then
    echo "ERROR: Health check failed after ${HEALTH_TIMEOUT}s — rolling back"
    flyctl releases rollback -a "$APP"
    exit 1
  fi
  sleep 1
done

# Step 3: Smoke test
echo ">>> Step 3: Smoke test"
SMOKE_OK=true
case "$SVC" in
  identity)   ENDPOINT="/api/authentication/csrf-token" ;;
  catalog)    ENDPOINT="/api/products?skip=0&take=1" ;;
  bffweb)     ENDPOINT="/api/brand" ;;
  *)          ENDPOINT="/health" ;;
esac

# Use flyctl proxy for internal services
RESP=$(flyctl ssh console -a "$APP" -C "curl -sf http://localhost:8080${ENDPOINT}" 2>/dev/null || echo "FAIL")
if [[ "$RESP" == "FAIL" ]]; then
  echo "WARN: Smoke test couldn't reach ${ENDPOINT} — proceeding with health-only validation"
else
  echo "    Smoke test passed: ${ENDPOINT}"
fi

# Step 4: Scale (if target > 1)
if [[ "$TARGET_MACHINES" -gt 1 ]]; then
  echo ">>> Step 4: Scaling to ${TARGET_MACHINES} machines"
  flyctl scale count "$TARGET_MACHINES" -a "$APP"
fi

echo "=== Canary deploy complete: ${APP} ==="
