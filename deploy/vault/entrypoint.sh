#!/bin/sh
# Start Vault in dev-mode in the background, wait for it to be ready,
# run the idempotent seed, then `wait` so PID 1 stays attached to vault.
#
# Required env (set in fly.vault.toml or via flyctl secrets):
#   VAULT_DEV_ROOT_TOKEN_ID  — root token (e.g. ritualworks-dev-root)
#
# Vault is bound to 0.0.0.0:8200 so it's reachable on Fly 6PN.
set -e

: "${VAULT_DEV_ROOT_TOKEN_ID:?VAULT_DEV_ROOT_TOKEN_ID must be set}"

export VAULT_ADDR="http://127.0.0.1:8200"
export VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"

# Start Vault dev-mode. Bind to [::]:8200 (IPv6 wildcard, dual-stack on
# Linux) so other Fly machines can reach us via 6PN — Fly's .internal
# DNS only returns IPv6, so binding to 0.0.0.0 (IPv4 only) leaves the
# vault unreachable from inside the cluster even though it's "up".
vault server \
  -dev \
  -dev-listen-address='[::]:8200' \
  -dev-root-token-id="$VAULT_DEV_ROOT_TOKEN_ID" &
VAULT_PID=$!

# Wait up to 30s for Vault to come up.
echo "[entrypoint] waiting for Vault to become ready..."
for i in $(seq 1 30); do
  if vault status >/dev/null 2>&1; then
    echo "[entrypoint] Vault is ready"
    break
  fi
  sleep 1
done

# Run seed in foreground; if it fails, log but keep Vault running so an
# operator can shell in and fix it manually.
if /usr/local/bin/seed.sh; then
  echo "[entrypoint] seed completed"
else
  echo "[entrypoint] WARN: seed.sh failed (Vault still serving)"
fi

# Stay attached to Vault.
wait "$VAULT_PID"
