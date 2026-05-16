#!/bin/sh
# Idempotent vault seed for prod-mode (raft on a Fly volume). Safe to run
# on every container start: every operation is create-if-missing or an
# upsert with the same value.
#
# Brings the Fly prod-mode setup to parity with deploy/aspire/vault-init.sh
# by consuming the SAME declarative manifests under infra/vault/ (mounted
# into the image at /vault/manifests/). One source of truth for the per-
# service AppRole + policy + dynamic-DB-role topology, dev and prod.
#
# Sets up:
#   * AppRole auth method
#   * Per-service policy svc-<name>      (from policies/service-template.hcl.tmpl)
#   * Per-service AppRole haworks-<name> (from auth/approle-template.json)
#   * KV v2 mount at secret/ + empty placeholder for secret/identity/jwt
#     so Identity's RotatingJwtSigningKeyRing can generate-and-write on
#     first boot
#   * Database secrets engine + per-bounded-context dynamic roles (when
#     VAULT_PG_OPERATOR_PASSWORD is staged)
#
# Differences from deploy/aspire/vault-init.sh (intentional):
#   * No /creds/<svc>/{role_id,secret_id} files written. Production stages
#     AppRole creds via deploy/fly/ci-stage-vault-creds.sh, which captures
#     them through the Vault HTTP API and pushes them to Fly secrets.
#   * No dev-placeholder KV values from kv-dev-values.json. Real secrets
#     are populated by an operator (`vault kv put ...`) or future Terraform.
#     We only seed an empty secret/identity/jwt so Identity can write its
#     first generated signing key without hitting "path does not exist"
#     metadata edge cases.
#   * Database creation_statements are simpler: the prod sandbox Postgres
#     (haworks-vault-pg) is a single instance with one shared user;
#     it does NOT have the per-database <db>_owner group roles that the
#     dev init-postgres.sql creates. So issued users get CONNECT + USAGE
#     on the default schema, no INHERIT IN ROLE.
set -eu

export VAULT_ADDR="${VAULT_ADDR:-http://127.0.0.1:8200}"

# VAULT_TOKEN must be set by the caller (entrypoint passes the root token).
if [ -z "${VAULT_TOKEN:-}" ]; then
  echo "[seed] ERROR: VAULT_TOKEN is required" >&2
  exit 1
fi

MANIFESTS="${MANIFESTS_DIR:-/vault/manifests}"
if [ ! -d "$MANIFESTS" ]; then
  echo "[seed] ERROR: manifests dir not found at $MANIFESTS" >&2
  echo "[seed]        the Dockerfile must COPY infra/vault/ into the image" >&2
  exit 1
fi

SERVICES_FILE="$MANIFESTS/services.json"
POLICY_TMPL="$MANIFESTS/policies/service-template.hcl.tmpl"
APPROLE_TMPL="$MANIFESTS/auth/approle-template.json"
DB_ROLES="$MANIFESTS/database/roles.json"

for f in "$SERVICES_FILE" "$POLICY_TMPL" "$APPROLE_TMPL" "$DB_ROLES"; do
  if [ ! -f "$f" ]; then
    echo "[seed] ERROR: required manifest missing: $f" >&2
    exit 1
  fi
done

# --- AppRole auth method --------------------------------------------------
echo "[seed] enabling AppRole auth (if missing)..."
if ! vault auth list -format=json | jq -e '."approle/"' >/dev/null 2>&1; then
  vault auth enable approle
fi

# --- KV v2 mount at secret/ ----------------------------------------------
# Some prod-mode vaults don't auto-mount secret/ the way dev-mode does.
# Idempotent — vault returns 400 if it already exists, which we swallow.
if ! vault secrets list -format=json | jq -e '."secret/"' >/dev/null 2>&1; then
  echo "[seed] enabling KV v2 at secret/..."
  vault secrets enable -path=secret -version=2 kv
fi

# --- Per-service policies + AppRoles -------------------------------------
# Loop pseudocode:
#   for svc in services.json:
#     render policies/service-template.hcl.tmpl with {{service}} = svc
#     vault policy write svc-<svc>
#     vault write auth/approle/role/haworks-<svc> ... token_policies=svc-<svc>
APPROLE_TTL=$(jq -r '.token_ttl'          "$APPROLE_TMPL")
APPROLE_MAX_TTL=$(jq -r '.token_max_ttl'  "$APPROLE_TMPL")
APPROLE_SECRET_TTL=$(jq -r '.secret_id_ttl'      "$APPROLE_TMPL")
APPROLE_SECRET_NUM=$(jq -r '.secret_id_num_uses' "$APPROLE_TMPL")

SERVICE_COUNT=$(jq -r '.services | length' "$SERVICES_FILE")
i=0
while [ "$i" -lt "$SERVICE_COUNT" ]; do
  SVC=$(jq -r ".services[$i].name" "$SERVICES_FILE")

  POLICY_NAME="svc-$SVC"
  POLICY_TMP=$(mktemp)
  sed "s/{{service}}/$SVC/g" "$POLICY_TMPL" > "$POLICY_TMP"
  vault policy write "$POLICY_NAME" "$POLICY_TMP" >/dev/null
  rm -f "$POLICY_TMP"
  echo "[seed] wrote policy $POLICY_NAME"

  APPROLE_NAME="haworks-$SVC"
  vault write "auth/approle/role/$APPROLE_NAME" \
    token_policies="$POLICY_NAME" \
    token_ttl="$APPROLE_TTL" \
    token_max_ttl="$APPROLE_MAX_TTL" \
    secret_id_ttl="$APPROLE_SECRET_TTL" \
    secret_id_num_uses="$APPROLE_SECRET_NUM" \
    bind_secret_id=true >/dev/null
  echo "[seed] configured AppRole $APPROLE_NAME -> $POLICY_NAME"

  i=$((i + 1))
done

# --- Identity JWT placeholder --------------------------------------------
# Identity's RotatingJwtSigningKeyRing reads-then-writes secret/identity/jwt
# on first boot. Pre-creating an empty entry keeps the metadata path warm
# so the first write doesn't race the implicit mount initialisation. All
# other services' real secrets are populated out-of-band by an operator.
#
# We do NOT put real values here. NEVER log the value either.
if ! vault kv get -format=json secret/identity/jwt >/dev/null 2>&1; then
  echo "[seed] writing empty placeholder at secret/identity/jwt"
  vault kv put secret/identity/jwt _placeholder=created-by-seed >/dev/null
fi

# ---------------------------------------------------------------------------
# Database secrets engine: dynamic short-TTL Postgres credentials.
#
# Vault issues per-request Postgres users for every haworks-<svc> role,
# scoped to the prod sandbox Postgres (haworks-vault-pg). The operator
# credentials Vault uses to CREATE ROLE on demand are supplied via env
# vars staged by deploy/fly/bootstrap.sh:
#
#   VAULT_PG_OPERATOR_USERNAME  (default "postgres")
#   VAULT_PG_OPERATOR_PASSWORD  (REQUIRED — no default; skip block if unset)
#
# When VAULT_PG_OPERATOR_PASSWORD is unset we log a warning and skip — the
# AppRole/KV setup above is still complete, so identity can boot with
# static secrets only.
# ---------------------------------------------------------------------------
VAULT_PG_OPERATOR_USERNAME="${VAULT_PG_OPERATOR_USERNAME:-postgres}"

if [ -z "${VAULT_PG_OPERATOR_PASSWORD:-}" ]; then
  echo "[seed] WARN: VAULT_PG_OPERATOR_PASSWORD not set — skipping database"
  echo "[seed]       secrets engine setup. Stage the secret via bootstrap.sh"
  echo "[seed]       once haworks-vault-pg is deployed, then redeploy."
else
  echo "[seed] enabling database secrets engine (if missing)..."
  if ! vault secrets list -format=json | jq -e '."database/"' >/dev/null 2>&1; then
    vault secrets enable database
  fi

  DB_DEFAULT_TTL=$(jq -r '.default_ttl' "$DB_ROLES")
  DB_MAX_TTL=$(jq -r '.max_ttl'         "$DB_ROLES")
  DB_ALLOWED_ROLES=$(jq -r '.roles | map(.role_name) | join(",")' "$DB_ROLES")

  # Configure the connection. `vault write database/config/<name>` is an
  # upsert: re-running with the same values is a no-op, and rotated
  # operator passwords flow through on next boot. allowed_roles enumerates
  # every role we're about to create — vault refuses to issue creds for a
  # role unless its name is in the connection's allow-list.
  echo "[seed] configuring database connection vault-pg (allowed_roles=$DB_ALLOWED_ROLES)..."
  vault write database/config/vault-pg \
    plugin_name=postgresql-database-plugin \
    allowed_roles="$DB_ALLOWED_ROLES" \
    connection_url="postgresql://{{username}}:{{password}}@haworks-vault-pg.internal:5432/postgres?sslmode=disable" \
    username="$VAULT_PG_OPERATOR_USERNAME" \
    password="$VAULT_PG_OPERATOR_PASSWORD" >/dev/null

  # Per-bounded-context dynamic roles. Simpler creation_statements than the
  # dev manifest's role-statements.json because the prod sandbox Postgres
  # has no <db>_owner group roles (single shared user, single database).
  # Issued users get LOGIN + CONNECT + USAGE on public schema only.
  ROLE_COUNT=$(jq -r '.roles | length' "$DB_ROLES")
  j=0
  while [ "$j" -lt "$ROLE_COUNT" ]; do
    ROLE_NAME=$(jq -r ".roles[$j].role_name" "$DB_ROLES")

    vault write "database/roles/$ROLE_NAME" \
      db_name=vault-pg \
      creation_statements="CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}'; GRANT CONNECT ON DATABASE postgres TO \"{{name}}\"; GRANT USAGE ON SCHEMA public TO \"{{name}}\";" \
      revocation_statements="DROP ROLE IF EXISTS \"{{name}}\";" \
      default_ttl="$DB_DEFAULT_TTL" \
      max_ttl="$DB_MAX_TTL" >/dev/null
    echo "[seed] configured dynamic role $ROLE_NAME"

    j=$((j + 1))
  done

  echo "[seed] database engine ready. Verify with:"
  echo "[seed]   vault read database/creds/haworks-identity"
fi

echo "[seed] done."
