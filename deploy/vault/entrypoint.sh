#!/bin/sh
# Vault prod-mode entrypoint. Three states the container can be in:
#
#  1. UNINITIALIZED — first ever run, /vault/data is empty. Run
#     `vault operator init` to generate one unseal key + a root token,
#     persist them to /vault/data/.init.json so bootstrap.sh can capture
#     and stage them as Fly secrets.
#
#  2. SEALED — vault has been initialized but the master key isn't loaded
#     into memory (always the case after a restart). Unseal using the
#     VAULT_UNSEAL_KEY env (set as a Fly secret by bootstrap.sh after the
#     first init), or fall back to the .init.json on disk for the
#     between-init-and-bootstrap-capture window.
#
#  3. UNSEALED — ready for clients. Run seed.sh once to make sure the
#     AppRole + policy + KV mounts are configured. seed.sh is idempotent;
#     re-running it on every boot is fine and keeps the demo's seed
#     contract documented in code.
#
# Vault's state lives on the Fly volume mounted at /vault/data, so the
# init keys are computed exactly once per cluster lifetime. Restarts /
# redeploys / OOMs all return to the same persisted state.
set -e

export VAULT_ADDR="http://127.0.0.1:8200"

# Start vault in the background. -log-level=warn keeps the container log
# focused on operational events; vault is chatty at info.
vault server -config=/vault/config/vault.hcl -log-level=warn &
VAULT_PID=$!

# Wait for the API to come up. `vault status` returns 0 if unsealed,
# 2 if sealed — both mean the listener is responsive.
echo "[entrypoint] waiting for vault listener..."
for i in $(seq 1 30); do
  if vault status >/dev/null 2>&1 || [ "$?" -eq 2 ]; then
    echo "[entrypoint] vault is responsive"
    break
  fi
  sleep 1
done

# State 1 → 2: initialize on empty volume.
INIT_FILE=/vault/data/.init.json
if vault status -format=json | jq -e '.initialized == false' >/dev/null 2>&1; then
  echo "[entrypoint] vault is uninitialized — running operator init"
  # Single-key shamir for initial launch. Upgrade path:
  #   1. Schedule a maintenance window
  #   2. vault operator rekey -init -key-shares=5 -key-threshold=3
  #   3. Distribute shares to separate operators/escrow
  #   4. OR migrate to cloud KMS auto-unseal (seal stanza in vault.hcl)
  # See: https://developer.hashicorp.com/vault/tutorials/operations/rekeying-and-rotating
  vault operator init -key-shares=1 -key-threshold=1 -format=json > "$INIT_FILE"
  chmod 600 "$INIT_FILE"
  echo "[entrypoint] operator init complete; keys stashed at $INIT_FILE"
fi

# State 2 → 3: unseal.
unseal_key() {
  if [ -n "${VAULT_UNSEAL_KEY:-}" ]; then
    echo "$VAULT_UNSEAL_KEY"
    return 0
  fi
  if [ -f "$INIT_FILE" ]; then
    jq -r '.unseal_keys_b64[0]' "$INIT_FILE"
    return 0
  fi
  return 1
}
if vault status -format=json | jq -e '.sealed == true' >/dev/null 2>&1; then
  if KEY="$(unseal_key)" && [ -n "$KEY" ]; then
    vault operator unseal "$KEY" >/dev/null
    echo "[entrypoint] vault unsealed"
  else
    echo "[entrypoint] WARN: no unseal key available — vault remains sealed"
    echo "[entrypoint]       set VAULT_UNSEAL_KEY (Fly secret) to auto-unseal"
  fi
fi

# State 3: run seed if we have a token. Same precedence: env var first
# (set by bootstrap.sh after first init), then the .init.json fallback.
seed_token() {
  if [ -n "${VAULT_ROOT_TOKEN_PROD:-}" ]; then
    echo "$VAULT_ROOT_TOKEN_PROD"
    return 0
  fi
  if [ -f "$INIT_FILE" ]; then
    jq -r '.root_token' "$INIT_FILE"
    return 0
  fi
  return 1
}
if vault status -format=json | jq -e '.sealed == false' >/dev/null 2>&1; then
  if TOKEN="$(seed_token)" && [ -n "$TOKEN" ]; then
    # Rotate audit log on startup. The 1GB Fly volume can fill up if the
    # audit log grows unbounded. Keep the previous run's log as .prev for
    # post-incident review; discard anything older.
    #
    # If the audit device is already enabled, we must disable it first so
    # Vault closes the file descriptor — otherwise the renamed file keeps
    # receiving writes via the old fd.
    AUDIT_LOG="/vault/data/audit.log"
    if [ -f "$AUDIT_LOG" ]; then
      if VAULT_TOKEN="$TOKEN" vault audit list -format=json 2>/dev/null | jq -e '."file/"' >/dev/null 2>&1; then
        VAULT_TOKEN="$TOKEN" vault audit disable file 2>/dev/null || true
      fi
      rm -f "${AUDIT_LOG}.prev"
      mv "$AUDIT_LOG" "${AUDIT_LOG}.prev"
      echo "[entrypoint] rotated audit log ($(wc -c < "${AUDIT_LOG}.prev" | tr -d ' ') bytes → .prev)"
    fi

    # Enable audit logging to a file on the persistent volume.
    # Required for compliance + post-incident review: every authenticated
    # Vault op (login, kv read, dynamic creds issuance, secret-id wrap)
    # gets a tamper-evident HMAC'd line.
    echo "[entrypoint] enabling audit log at $AUDIT_LOG"
    VAULT_TOKEN="$TOKEN" vault audit enable file file_path="$AUDIT_LOG" >/dev/null 2>&1 || true

    echo "[entrypoint] running seed.sh"
    if VAULT_TOKEN="$TOKEN" /usr/local/bin/seed.sh; then
      echo "[entrypoint] seed complete"
    else
      echo "[entrypoint] WARN: seed.sh failed (vault still serving)"
    fi
  else
    echo "[entrypoint] WARN: no root token — skipping seed"
  fi
fi

# Stay attached to vault.
wait "$VAULT_PID"
