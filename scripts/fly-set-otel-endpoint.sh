#!/usr/bin/env bash
# =============================================================================
# Sets OTEL_EXPORTER_OTLP_ENDPOINT secret on every .NET app in the platform.
#
# Why this exists: the Fly toml [env] blocks already carry the static OTel
# identity attributes (service.name, deployment.environment, service.namespace)
# but Fly env values do NOT interpolate ${VAR}, so a per-environment URL has
# to live as a secret and be applied app-by-app. This loops the apps so the
# operator does not have to remember the exact list or the syntax.
#
# Usage:
#   ./scripts/fly-set-otel-endpoint.sh <otlp-endpoint>
#
# Examples:
#   ./scripts/fly-set-otel-endpoint.sh https://tempo-prod-04-prod-us-east-0.grafana.net:443
#   ./scripts/fly-set-otel-endpoint.sh https://api.honeycomb.io:443
#   ./scripts/fly-set-otel-endpoint.sh http://otel-gw.internal:4317
#
# Skips infra apps (haworks-vault, haworks-vault-pg,
# haworks-meilisearch) — they have no .NET runtime and no OTel SDK loaded.
#
# Each `fly secrets set` call triggers a rolling redeploy of that app's
# machines. Expect ~30s per app while machines roll. Run from a host with
# `flyctl auth login` already done.
#
# See docs/runbooks/observability-fly-otlp-secret.md for the full operator
# story (rotation, verification, what-if-I-forget, choosing a backend).
# =============================================================================
set -euo pipefail

# ---- arg parsing -----------------------------------------------------------

usage() {
    cat <<EOF
Usage: $(basename "$0") <otlp-endpoint>

Sets OTEL_EXPORTER_OTLP_ENDPOINT on every .NET Fly app in the platform.

The endpoint must start with http:// or https:// and point at an OTLP
collector (Tempo, Honeycomb, Grafana Cloud, self-hosted otel-collector,
etc). Both HTTP/protobuf (port 4318) and gRPC (port 4317) are supported by
the SDK; the URL scheme is what matters here.

Examples:
  $(basename "$0") https://tempo-prod-04-prod-us-east-0.grafana.net:443
  $(basename "$0") https://api.honeycomb.io:443
EOF
}

if [[ $# -ne 1 ]]; then
    usage >&2
    exit 1
fi

URL="$1"

if [[ ! "$URL" =~ ^https?:// ]]; then
    echo "ERROR: endpoint must start with http:// or https:// (got: $URL)" >&2
    usage >&2
    exit 1
fi

# ---- app list --------------------------------------------------------------
#
# Logical service names map to Fly app names with the `haworks-` prefix
# (cf. fly.*.toml `app =` line). Keep this list in sync with the .NET apps
# that have OTel auto-instrumentation wired in BuildingBlocks.Observability.
#
# Skipped on purpose:
#   haworks-vault, haworks-vault-pg  — Hashicorp Vault + Postgres,
#                                              no .NET, no OTel SDK.
#   haworks-meilisearch                  — vendor image, emits its own
#                                              telemetry on a different path.
APPS=(
    haworks-bffweb
    haworks-catalog
    haworks-checkout
    haworks-content
    haworks-identity
    haworks-notifications
    haworks-orders
    haworks-payments
    haworks-search
    haworks-audit
)

# ---- main loop -------------------------------------------------------------

if ! command -v flyctl >/dev/null 2>&1; then
    echo "ERROR: flyctl not on PATH. Install from https://fly.io/docs/hands-on/install-flyctl/" >&2
    exit 1
fi

# Track per-app outcomes for the summary table at the end. We deliberately do
# NOT abort the loop on a single failure — a missing app (e.g. audit-svc not
# yet provisioned) should not block applying the secret to the other 9.
declare -a RESULTS=()

# Disable -e inside the loop so a single failed app does not kill the script.
# We re-check the rc explicitly per call.
set +e
for APP in "${APPS[@]}"; do
    echo
    echo "===> $APP"
    if flyctl secrets set "OTEL_EXPORTER_OTLP_ENDPOINT=$URL" -a "$APP"; then
        RESULTS+=("OK    $APP")
    else
        RESULTS+=("FAIL  $APP")
    fi
done
set -e

# ---- summary ---------------------------------------------------------------

echo
echo "============================================================"
echo "OTEL_EXPORTER_OTLP_ENDPOINT applied to:"
echo "  $URL"
echo "------------------------------------------------------------"
printf '  %s\n' "${RESULTS[@]}"
echo "============================================================"

# Exit non-zero if anything failed, so CI / chained scripts notice.
for R in "${RESULTS[@]}"; do
    case "$R" in
        FAIL*) exit 2 ;;
    esac
done
