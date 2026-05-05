#!/usr/bin/env bash
# =============================================================================
# Aspire AppHost wrapper that:
#   1. Pre-cleans orphan .NET service processes from prior runs.
#   2. Launches deploy/aspire/RitualworksPlatform.AppHost with a signal trap
#      that kills *its* child service processes on exit (Ctrl+C, terminal
#      hangup, kill).
#
# Why this exists
# ---------------
# .NET Aspire on macOS frequently leaves orphan child processes after the
# AppHost terminates abruptly (Ctrl+C in some shells, terminal close, kill
# without graceful shutdown, IDE crash). Each `dotnet run --no-build` Aspire
# spawns is actually a wrapper that forks the real `<svc>.Api` binary; when
# the wrapper dies the child can detach and survive.
#
# The downstream effect is that the next Aspire start fights with the
# orphans for the dynamically-allocated port that Aspire's dcpctrl reverse
# proxy is forwarding to. The proxy frequently routes to a stale orphan
# whose DB pool is dead and whose Vault token has expired, which surfaces
# as mysterious "service unreachable" timeouts in the freshly-started BffWeb.
#
# This wrapper makes the Aspire start idempotent: previous runs' debris is
# cleaned, current run's children are tracked + killed on exit.
#
# Usage
# -----
#   ./scripts/aspire-up.sh              # build + run
#   ./scripts/aspire-up.sh --no-build   # skip build
#   SKIP_PRECLEAN=1 ./scripts/aspire-up.sh   # skip the pre-clean step
# =============================================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPHOST_PROJECT="$REPO_ROOT/deploy/aspire/RitualworksPlatform.AppHost.csproj"

# Service binaries Aspire spawns under bin/Debug/ — match all of them but
# nothing else. Path-anchored to this repo so we don't touch unrelated
# dotnet processes that happen to share a name.
SERVICE_BIN_PATTERN="${REPO_ROOT}/src/(Catalog|Orders|Identity|Payments|CheckoutOrchestrator|BffWeb)/[^/]+\.Api/bin/Debug"
DOTNET_RUN_PATTERN="dotnet run --no-build --project ${REPO_ROOT}/src"

clean_orphans() {
  local label="$1"
  # Two passes: child API binaries first, then the dotnet-run wrappers that
  # spawned them. Empty greps are not failures (set +e around grep itself).
  set +e
  local svc_pids
  svc_pids=$(pgrep -f "$SERVICE_BIN_PATTERN" || true)
  local wrap_pids
  wrap_pids=$(pgrep -f "$DOTNET_RUN_PATTERN" || true)
  set -e

  if [[ -z "$svc_pids" && -z "$wrap_pids" ]]; then
    echo "[aspire-up] $label: no orphan service processes found."
    return 0
  fi

  echo "[aspire-up] $label: killing orphan service processes:"
  if [[ -n "$svc_pids" ]]; then
    echo "  service binaries: $(echo "$svc_pids" | tr '\n' ' ')"
    kill -TERM $svc_pids 2>/dev/null || true
  fi
  if [[ -n "$wrap_pids" ]]; then
    echo "  dotnet-run wrappers: $(echo "$wrap_pids" | tr '\n' ' ')"
    kill -TERM $wrap_pids 2>/dev/null || true
  fi

  # Give them a beat to exit cleanly, then SIGKILL anything still alive.
  sleep 2
  set +e
  svc_pids=$(pgrep -f "$SERVICE_BIN_PATTERN" || true)
  wrap_pids=$(pgrep -f "$DOTNET_RUN_PATTERN" || true)
  set -e
  if [[ -n "$svc_pids" || -n "$wrap_pids" ]]; then
    echo "[aspire-up] $label: SIGKILLing stragglers."
    [[ -n "$svc_pids" ]] && kill -KILL $svc_pids 2>/dev/null || true
    [[ -n "$wrap_pids" ]] && kill -KILL $wrap_pids 2>/dev/null || true
  fi
  echo "[aspire-up] $label: done."
}

if [[ "${SKIP_PRECLEAN:-0}" != "1" ]]; then
  clean_orphans "pre-start"
fi

# On exit (normal or signalled) clean again so we don't leave debris for the
# next run. Trap covers EXIT (covers normal completion + ERR via set -e),
# plus the explicit signals so we exit before EXIT runs the trap with the
# right disposition. INT/TERM/HUP cover Ctrl+C, kill, and terminal close.
on_shutdown() {
  echo ""
  echo "[aspire-up] AppHost exiting; cleaning child processes..."
  clean_orphans "post-stop"
}
trap on_shutdown EXIT

cd "$REPO_ROOT"

# Default to --no-build because the user almost always runs this AFTER a
# successful `dotnet build`; pass --build to opt in.
ARGS=()
if [[ "${1:-}" == "--build" ]]; then
  shift
  echo "[aspire-up] dotnet build..."
  dotnet build "$APPHOST_PROJECT" -c Debug
fi

# NOTE: do NOT `exec` here — exec replaces the shell process, so our EXIT
# trap (which cleans up orphans on shutdown) would never fire. Instead run
# in foreground with `wait` and forward SIGINT/SIGTERM/SIGHUP to the child.
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

DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}" \
ASPIRE_ALLOW_UNSECURED_TRANSPORT="${ASPIRE_ALLOW_UNSECURED_TRANSPORT:-true}" \
  dotnet run --project "$APPHOST_PROJECT" --no-build "$@" &
APP_PID=$!

# `wait` on a single PID returns the exit status of that PID. The `|| true`
# is to keep `set -e` from killing us before the EXIT trap can run cleanup
# when the AppHost exits non-zero (which happens on Ctrl+C).
wait "$APP_PID" || true

