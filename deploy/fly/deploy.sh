#!/usr/bin/env bash
# Deploy services in dependency order:
#   1. identity                                    (others may auth at boot)
#   2. catalog, orders, payments, checkout, content (parallel)
#   3. bffweb                                      (talks to all backends)
#
# DEPLOY_CONTENT=true (in .env.local) opts into content-svc; default skips it.
# Run bootstrap.sh first (or any time .env.local changes) to stage secrets.

set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

ENV_FILE="$ROOT_DIR/deploy/fly/.env.local"
DEPLOY_CONTENT="false"
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi
DEPLOY_CONTENT="${DEPLOY_CONTENT:-false}"

deploy_one() {
  local svc="$1"
  echo ">>> deploying $svc"
  # --ha=false → 1 machine per app instead of Fly's default of 2 (HA pair).
  # 7 services × 2 machines = 14, which busts the free-tier cap. Single-
  # machine deploys + auto-stop are plenty for portfolio traffic; flip to
  # `flyctl scale count N -a ritualworks-<svc>` after upgrading the org if
  # you want real HA.
  flyctl deploy -c "fly.${svc}.toml" --remote-only --ha=false
}

deploy_one identity

PARALLEL=(catalog orders payments checkout audit search webhooks)
[[ "$DEPLOY_CONTENT" == "true" ]] && PARALLEL+=(content)

pids=()
for svc in "${PARALLEL[@]}"; do
  ( deploy_one "$svc" ) &
  pids+=($!)
done
fail=0
for pid in "${pids[@]}"; do
  wait "$pid" || fail=1
done
[[ $fail -eq 0 ]] || { echo "Backend deploy failed; aborting BFF" >&2; exit 1; }

deploy_one bffweb
echo "All services deployed."
