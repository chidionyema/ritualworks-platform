#!/usr/bin/env bash
# =============================================================================
# Aspire AppHost wrapper that:
#   1. Pre-cleans orphan .NET service processes from prior runs.
#   2. Performs Docker preflight and drift checks.
#   3. Launches AppHost with per-resource log capture (via Program.cs).
#   4. Monitors child health and crashes via a watchdog.
# =============================================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPHOST_PROJECT="$REPO_ROOT/deploy/aspire/HaworksPlatform.AppHost.csproj"
LOGS_DIR="$REPO_ROOT/logs"
mkdir -p "$LOGS_DIR"

# Docker preflight
if ! docker info >/dev/null 2>&1; then
  echo "Error: Docker is not running. Please start Docker Desktop and try again."
  exit 2
fi

# Flags
NO_BUILD=0
NO_WATCHDOG=0
ARGS=()

while [[ $# -gt 0 ]]; do
  case $1 in
    --no-build) NO_BUILD=1; shift ;;
    --no-watchdog) NO_WATCHDOG=1; shift ;;
    --build) NO_BUILD=0; shift ;; 
    *) ARGS+=("$1"); shift ;;
  esac
done

# Source-vs-binary drift check
if [[ "$NO_BUILD" == "1" ]]; then
  echo "[aspire-up] Checking for source drift..."
  STALE_FILES=()
  for svc in Catalog Orders Identity Payments CheckoutOrchestrator BffWeb; do
    SVC_DIR="$REPO_ROOT/src/$svc/$svc.Api"
    if [[ ! -d "$SVC_DIR" ]]; then continue; fi
    
    DLL=$(find "$SVC_DIR/bin/Debug" -name "$svc.Api.dll" | head -n 1 || true)
    if [[ -z "$DLL" ]]; then
      echo "Error: Binary not found for $svc. Run without --no-build first."
      exit 3
    fi
    
    NEWER_SOURCES=$(find "$SVC_DIR" -name "*.cs" -newer "$DLL" || true)
    if [[ -n "$NEWER_SOURCES" ]]; then
      for f in $NEWER_SOURCES; do STALE_FILES+=("$f"); done
    fi
  done
  
  if [[ ${#STALE_FILES[@]} -gt 0 ]]; then
    echo "Error: Source drift detected! The following files are newer than the binaries:"
    for f in "${STALE_FILES[@]}"; do echo "  - $f"; done
    echo "Please rebuild or drop --no-build."
    exit 3
  fi
fi

# Service patterns for cleaning
SERVICE_BIN_PATTERN="${REPO_ROOT}/src/(Catalog|Orders|Identity|Payments|CheckoutOrchestrator|BffWeb)/[^/]+\.Api/bin/Debug"
DOTNET_RUN_PATTERN="dotnet run --no-build --project ${REPO_ROOT}/src"
APPHOST_PATTERN="${REPO_ROOT}/deploy/aspire/(bin/Debug/[^/]+/HaworksPlatform\\.AppHost|HaworksPlatform\\.AppHost\\.csproj)"
DCPCTRL_PATTERN="aspire\\.hosting\\.orchestration\\.[^/]+/[^/]+/tools/ext/dcpctrl"

clean_orphans() {
  local label="$1"
  set +e
  local pids
  pids=$(pgrep -f "$SERVICE_BIN_PATTERN" || true)
  pids="$pids $(pgrep -f "$DOTNET_RUN_PATTERN" || true)"
  pids="$pids $(pgrep -f "$APPHOST_PATTERN" || true)"
  pids="$pids $(pgrep -f "$DCPCTRL_PATTERN" || true)"
  set -e

  if [[ -z "${pids// }" ]]; then
    echo "[aspire-up] $label: no orphan service processes found."
    return 0
  fi

  echo "[aspire-up] $label: killing orphan processes..."
  kill -TERM $pids 2>/dev/null || true
  sleep 2
  kill -KILL $pids 2>/dev/null || true
  echo "[aspire-up] $label: done."
}

if [[ "${SKIP_PRECLEAN:-0}" != "1" ]]; then
  clean_orphans "pre-start"
fi

on_shutdown() {
  echo ""
  echo "[aspire-up] AppHost exiting; cleaning child processes..."
  clean_orphans "post-stop"
}
trap on_shutdown EXIT

cd "$REPO_ROOT"

if [[ "$NO_BUILD" == "0" ]]; then
  echo "[aspire-up] dotnet build..."
  dotnet build "$APPHOST_PROJECT" -c Debug
fi

APP_PID=
forward_signal() {
  local sig="$1"
  if [[ -n "$APP_PID" ]] && kill -0 "$APP_PID" 2>/dev/null; then
    kill -"$sig" "$APP_PID" 2>/dev/null || true
  fi
}
trap 'forward_signal INT' INT
trap 'forward_signal TERM' TERM
trap 'forward_signal HUP' HUP

echo "[aspire-up] Launching AppHost..."
DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}" \
ASPIRE_LOGS_DIR="$LOGS_DIR" \
ASPIRE_ALLOW_UNSECURED_TRANSPORT="${ASPIRE_ALLOW_UNSECURED_TRANSPORT:-true}" \
  dotnet run --project "$APPHOST_PROJECT" --no-build "${ARGS[@]}" > "$LOGS_DIR/apphost.log" 2>&1 &
APP_PID=$!

if [[ "$NO_WATCHDOG" == "1" ]]; then
  wait "$APP_PID" || true
  exit 0
fi

# Watchdog Logic
echo "[aspire-up] Watchdog started (waiting for dashboard and bff-web)..."

# The dashboard's gRPC endpoint (used by WatchResources) is logged when the
# dashboard hosts its own self-RPC, e.g.:
#   Request starting HTTP/2 POST https://localhost:22000/aspire.v1.DashboardService/...
# That's the URL we need — NOT the web UI on :17000 ("Now listening on:" /
# "Login to the dashboard at"). Web UI port doesn't speak gRPC.
DASHBOARD_URL=""
MAX_WAIT=30
while [[ $MAX_WAIT -gt 0 ]]; do
  DASHBOARD_URL=$(grep -oE "https://localhost:[0-9]+/aspire\.v1\.DashboardService" "$LOGS_DIR/apphost.log" 2>/dev/null \
    | head -n 1 | sed 's|/aspire.v1.DashboardService||' || true)
  if [[ -n "$DASHBOARD_URL" ]]; then break; fi
  sleep 1
  MAX_WAIT=$((MAX_WAIT - 1))
done

if [[ -z "$DASHBOARD_URL" ]]; then
  echo "[aspire-up] Note: dashboard gRPC endpoint not yet discoverable; gRPC fast-detect disabled. Health gate still active."
fi

GATE_URL="http://localhost:5050/health"
GATE_TIMEOUT=90
START_TIME=$(date +%s)
GATE_PASSED=0

check_crashes() {
  if command -v grpcurl >/dev/null 2>&1 && [[ -n "$DASHBOARD_URL" ]]; then
    ADDR=${DASHBOARD_URL#https://}
    RESP=$(timeout 2 grpcurl -insecure "$ADDR" aspire.v1.DashboardService/WatchResources 2>/dev/null || true)
    if echo "$RESP" | grep -qE "state: \"(Stopped|FailedToStart)\""; then
        FAILED_SVC=$(echo "$RESP" | grep -B 2 -E "state: \"(Stopped|FailedToStart)\"" | grep "resource_id" | head -n 1 | awk '{print $2}' | tr -d '"')
        echo "Error: Service $FAILED_SVC entered a failure state."
        # Try to find the log file (handle random suffixes)
        LOG_FILE=$(ls "$LOGS_DIR" | grep "^$FAILED_SVC" | head -n 1 || true)
        echo "Check logs at: $LOGS_DIR/${LOG_FILE:-$FAILED_SVC.log}"
        kill "$APP_PID"
        exit 1
    fi
  fi
}

while true; do
  if ! kill -0 "$APP_PID" 2>/dev/null; then
    echo "Error: AppHost process died unexpectedly."
    exit 1
  fi

  check_crashes

  if [[ "$GATE_PASSED" == "0" ]]; then
    STATUS=$(curl -s -o /dev/null -w "%{http_code}" "$GATE_URL" || echo "000")
    if [[ "$STATUS" == "200" ]]; then
      echo "[aspire-up] Health gate passed (200 OK)."
      GATE_PASSED=1
      echo "[aspire-up] Stack is healthy. Monitoring for crashes..."
    fi

    CURRENT_TIME=$(date +%s)
    ELAPSED=$((CURRENT_TIME - START_TIME))
    if [[ $ELAPSED -gt $GATE_TIMEOUT && "$GATE_PASSED" == "0" ]]; then
      echo "Error: Health gate timeout after ${GATE_TIMEOUT}s."
      # ResourceFileLogger creates per-run files like bff-web-<8char>.log;
      # pick the newest (most likely to hold this run's failure).
      LATEST_BFF_LOG=$(ls -t "$LOGS_DIR"/bff-web-*.log 2>/dev/null | head -n 1 || true)
      if [[ -n "$LATEST_BFF_LOG" ]]; then
        echo "Last 50 lines of $LATEST_BFF_LOG:"
        tail -n 50 "$LATEST_BFF_LOG"
      else
        echo "(no bff-web log file in $LOGS_DIR — service may have failed before logger started)"
      fi
      kill "$APP_PID"
      exit 1
    fi
  fi

  sleep 2
done

wait "$APP_PID" || true
