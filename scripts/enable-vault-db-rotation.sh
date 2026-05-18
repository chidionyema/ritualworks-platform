#!/usr/bin/env bash
# Enable Vault database credential rotation for a service against vault-pg.
# Usage: ./scripts/enable-vault-db-rotation.sh identity
#
# Prerequisites:
#   - haworks-vault-pg deployed with init.sql (users created)
#   - haworks-vault deployed with seed.sh (static roles created)
#   - Service deployed with Vault__Enabled=true
set -euo pipefail

SERVICE="${1:?Usage: $0 <service-name>}"
FLY_APP="haworks-${SERVICE}"

# Stage the DatabaseMode + vault-pg connection details
flyctl secrets set --stage -a "$FLY_APP" \
  "Vault__DatabaseMode=StaticRole" \
  "Database__Host=haworks-vault-pg.internal" \
  "Database__Port=5432" \
  "Database__Database=postgres" \
  "Database__SslMode=Disable"

echo "Staged StaticRole mode on $FLY_APP. Deploy to activate."
