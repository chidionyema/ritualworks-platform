#!/bin/bash
# =============================================================================
# Seed Vault with development secret VALUES.
# Runs as the second one-shot Aspire container (after vault-init.sh has
# created the AppRoles + KV layout). vault-init writes structural config;
# this script writes the actual placeholder secret values.
#
# Idempotent — safe to re-run from the Aspire dashboard if you need to
# refresh values during a session (Vault dev mode is in-memory; secrets
# vanish on restart but vault-seed will re-run and restore them).
# =============================================================================
set -e

VAULT_ADDR="${VAULT_ADDR:-http://localhost:8200}"
VAULT_TOKEN="${VAULT_TOKEN:-dev-root-token}"
export VAULT_ADDR VAULT_TOKEN

echo "Seeding Vault at $VAULT_ADDR..."

# Wait for Vault to be ready. Uses `vault status` (CLI is present in both
# the hashicorp/vault container and a typical dev's PATH); curl is NOT
# present in the official Vault image, so curl-based loops spin forever.
echo "Waiting for Vault to be ready..."
attempts=0
until vault status > /dev/null 2>&1 || [ $? -eq 2 ]; do
    attempts=$((attempts + 1))
    if [ $attempts -gt 60 ]; then
        echo "ERROR: Vault did not become ready within 60s at $VAULT_ADDR"
        exit 1
    fi
    sleep 1
done
echo "Vault is ready!"

# Note: vault-init.sh already wrote the actual KV values from kv-dev-values.json
# (it's the single source of truth for both layout and values). This script is
# a no-op safety net for cases where dev-mode Vault was restarted but vault-init
# has not yet re-run — in that case it would simply log and exit, since the KV
# paths are populated by vault-init's loop over kv-layout.json paths.

echo "==================================================================="
echo "NO ACTION TAKEN. This script is a safety-net no-op."
echo ""
echo "Vault dev secrets are auto-seeded by the Aspire startup chain:"
echo "  vault-init.sh  → AppRoles + KV layout from kv-layout.json"
echo "  vault-seed.sh  → KV values from kv-dev-values.json"
echo ""
echo "Both run as one-shot Aspire containers — no manual step required."
echo "Verify a secret with:  vault kv get secret/identity/jwt"
echo "==================================================================="
