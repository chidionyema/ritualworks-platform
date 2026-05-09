#!/bin/sh
# Identity entrypoint shim — minimal.
#
# With persistent vault (raft on a Fly volume) and bootstrap-time direct
# cred staging (Vault__RoleId / Vault__SecretId as Fly secrets), the
# .NET app reads its AppRole creds straight from configuration. The
# legacy "fetch from vault at boot" path that this shim used to host is
# dead code; deleted.
#
# What's left: write the direct creds to disk so the few legacy
# consumers that still read VaultOptions.RoleIdPath/SecretIdPath
# (VaultClientFactory, runtime IVaultService cred refresh) find the
# files where they expect them. Then exec the .NET app.
#
# When Vault is disabled (e.g. local dev without a vault container),
# skip the disk write and exec straight through.
set -e

if [ -n "${Vault__RoleId:-}" ] && [ -n "${Vault__SecretId:-}" ]; then
  ROLE_ID_PATH="${Vault__RoleIdPath:-/tmp/vault/role_id}"
  SECRET_ID_PATH="${Vault__SecretIdPath:-/tmp/vault/secret_id}"
  mkdir -p "$(dirname "$ROLE_ID_PATH")" "$(dirname "$SECRET_ID_PATH")"
  printf '%s' "$Vault__RoleId"   > "$ROLE_ID_PATH"
  printf '%s' "$Vault__SecretId" > "$SECRET_ID_PATH"
  chmod 600 "$ROLE_ID_PATH" "$SECRET_ID_PATH"
fi

exec dotnet Identity.Api.dll
