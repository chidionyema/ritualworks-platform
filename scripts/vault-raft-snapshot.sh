#!/usr/bin/env bash
# Take a Vault raft snapshot and store it locally. Designed to run from an
# operator laptop or CI on a schedule. The snapshot contains the full Vault
# state (policies, AppRoles, KV secrets, PKI CA, database config) and can
# restore a Vault cluster from scratch.
#
# Usage:
#   ./scripts/vault-raft-snapshot.sh                 # snapshot to ./vault-snapshots/
#   SNAPSHOT_DIR=/backups ./scripts/vault-raft-snapshot.sh  # custom output dir
#
# Prerequisites:
#   - flyctl authenticated (FLY_API_TOKEN or `flyctl auth login`)
#   - VAULT_CI_TOKEN staged as a Fly secret on haworks-vault
#
# Restore:
#   flyctl ssh console -a haworks-vault -C \
#     "vault operator raft snapshot restore /vault/data/restore.snap"
set -euo pipefail

VAULT_APP="${VAULT_APP:-haworks-vault}"
SNAPSHOT_DIR="${SNAPSHOT_DIR:-./vault-snapshots}"
TIMESTAMP=$(date -u +%Y%m%d-%H%M%SZ)
SNAPSHOT_FILE="$SNAPSHOT_DIR/vault-raft-$TIMESTAMP.snap"

mkdir -p "$SNAPSHOT_DIR"

echo "[vault-snapshot] fetching VAULT_CI_TOKEN from $VAULT_APP..."
ci_token=$(flyctl ssh console -a "$VAULT_APP" -C 'sh -c "printenv VAULT_CI_TOKEN"' \
  | tr -d '\r' | sed '/^Connecting to/d' | head -1)

if [[ -z "$ci_token" || "$ci_token" == "null" ]]; then
  echo "[vault-snapshot] ERROR: VAULT_CI_TOKEN not available on $VAULT_APP"
  exit 1
fi

echo "[vault-snapshot] taking raft snapshot..."
flyctl ssh console -a "$VAULT_APP" -C \
  "sh -c \"curl -fsS -H 'X-Vault-Token: $ci_token' http://[::1]:8200/v1/sys/storage/raft/snapshot -o /tmp/snapshot.snap && cat /tmp/snapshot.snap && rm /tmp/snapshot.snap\"" \
  > "$SNAPSHOT_FILE"

size=$(wc -c < "$SNAPSHOT_FILE" | tr -d ' ')
echo "[vault-snapshot] saved $SNAPSHOT_FILE ($size bytes)"

# Prune snapshots older than 30 days
find "$SNAPSHOT_DIR" -name "vault-raft-*.snap" -mtime +30 -delete 2>/dev/null || true
echo "[vault-snapshot] pruned snapshots older than 30 days"
