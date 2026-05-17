#!/usr/bin/env bash
# Smoke test: verify all services with DB access are healthy after
# credential rotation wiring is in place.
set -euo pipefail

SERVICES=(
  "identity:8080"
  "catalog:8081"
  "orders:8082"
  "payments:8083"
  "content:8084"
  "checkout-orchestrator:8085"
  "notifications:8086"
)

FAILED=0

for entry in "${SERVICES[@]}"; do
  IFS=':' read -r svc port <<< "$entry"
  if curl -sf "http://localhost:${port}/health/ready" > /dev/null 2>&1; then
    echo "OK: $svc"
  else
    echo "FAIL: $svc (port $port)"
    FAILED=1
  fi
done

if [ "$FAILED" -eq 0 ]; then
  echo "All services healthy after credential rotation wiring"
  exit 0
else
  echo "Some services failed health check"
  exit 1
fi
