#!/usr/bin/env bash
set -euo pipefail

# Usage: ./scripts/run-stryker.sh [service]
# Services: payments, payments-application, media, checkout-orchestrator, all
# Default: all

SERVICE="${1:-all}"
STRYKER_DIR="stryker"
OUTPUT_DIR="StrykerOutput"

# Ensure Stryker is installed
if ! command -v dotnet-stryker &>/dev/null; then
  echo "Installing Stryker.NET..."
  dotnet tool install --global dotnet-stryker
fi

run_stryker() {
  local name="$1"
  local config="$2"
  echo ""
  echo "══════════════════════════════════════════════════"
  echo "  Running Stryker: $name"
  echo "══════════════════════════════════════════════════"
  echo ""
  dotnet stryker --config-file "$config" --output "$OUTPUT_DIR/$name" || true
  echo ""
  echo "Report: $OUTPUT_DIR/$name/reports/mutation-report.html"
}

case "$SERVICE" in
  payments)
    run_stryker "payments-domain" "$STRYKER_DIR/payments.json"
    ;;
  payments-application)
    run_stryker "payments-application" "$STRYKER_DIR/payments-application.json"
    ;;
  media)
    run_stryker "media" "$STRYKER_DIR/media.json"
    ;;
  checkout-orchestrator)
    run_stryker "checkout-orchestrator" "$STRYKER_DIR/checkout-orchestrator.json"
    ;;
  all)
    run_stryker "payments-domain" "$STRYKER_DIR/payments.json"
    run_stryker "payments-application" "$STRYKER_DIR/payments-application.json"
    run_stryker "media" "$STRYKER_DIR/media.json"
    run_stryker "checkout-orchestrator" "$STRYKER_DIR/checkout-orchestrator.json"
    ;;
  *)
    echo "Unknown service: $SERVICE"
    echo "Usage: $0 [payments|payments-application|media|checkout-orchestrator|all]"
    exit 1
    ;;
esac

echo ""
echo "Done. Open HTML reports in StrykerOutput/*/reports/"
