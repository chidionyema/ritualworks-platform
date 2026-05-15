#!/bin/sh
# Bootstraps Vault with the per-service prod-shaped configuration each
# microservice expects. Consumes shared declarative manifests under
# /manifests (mounted from repo `infra/vault/`) — same files prod IaC reads.
#
# ============================================================================
# DEVELOPMENT ONLY. DO NOT RUN OUTSIDE LOCAL DEV ENVIRONMENTS.
# ----------------------------------------------------------------------------
# This script seeds dev-placeholder secrets from kv-dev-values.json. Real
# environments must replicate the same Vault SHAPE via your IaC pipeline,
# pulling REAL secret values from your secrets store.
# ============================================================================
set -eu

if [ "${ALLOW_DEV_SEED:-no}" != "yes" ]; then
    cat >&2 <<'EOF'
[vault-init] REFUSING TO RUN: this script seeds DEV-ONLY placeholders into Vault.
[vault-init] Set ALLOW_DEV_SEED=yes to confirm intent. The Aspire AppHost
[vault-init] sets this env var automatically. If you're running by hand,
[vault-init] you almost certainly want the prod-equivalent IaC pipeline.
EOF
    exit 2
fi

MANIFESTS="${MANIFESTS_DIR:-/manifests}"
[ -d "$MANIFESTS" ] || { echo "[vault-init] FATAL: manifests dir not found at $MANIFESTS" >&2; exit 1; }

log() { echo "[vault-init] $*"; }

# jq is not in the base hashicorp/vault image; install on first run.
if ! command -v jq >/dev/null 2>&1; then
    log "Installing jq (one-shot)"
    apk add --no-cache jq >/dev/null
fi

log "Waiting for Vault at $VAULT_ADDR ..."
until vault status >/dev/null 2>&1; do sleep 1; done
log "Vault is up"

# Idempotent short-circuit. Both /creds and the vault container's storage
# are persistent across `dotnet run` invocations (Aspire ContainerLifetime.Persistent
# + bind-mounted creds dir). If a prior run already populated AppRole creds
# AND the vault still has the corresponding policies, there's nothing to do.
# Saves ~20-30s on warm boots — the slowest part of every restart.
SENTINEL="/creds/.init-completed"
SENTINEL_VERSION="v2"
if [ -f "$SENTINEL" ] \
    && [ "$(cat "$SENTINEL" 2>/dev/null)" = "$SENTINEL_VERSION" ] \
    && [ -s "/creds/identity/role_id" ] \
    && vault auth list 2>/dev/null | grep -q '^approle/' \
    && vault policy list 2>/dev/null | grep -q '^svc-identity'; then
    log "Already initialised (sentinel + AppRole present); skipping setup."
    exit 0
fi

# --- AppRole auth method --------------------------------------------------
if ! vault auth list 2>/dev/null | grep -q '^approle/'; then
    vault auth enable approle
    log "Enabled AppRole auth method"
fi

# --- Per-service policies + AppRoles + credential files ------------------
SERVICES_FILE="$MANIFESTS/services.json"
POLICY_TMPL="$MANIFESTS/policies/service-template.hcl.tmpl"
APPROLE_TMPL="$MANIFESTS/auth/approle-template.json"

mkdir -p /creds

SERVICE_COUNT=$(jq -r '.services | length' "$SERVICES_FILE")
i=0
while [ "$i" -lt "$SERVICE_COUNT" ]; do
    SVC=$(jq -r ".services[$i].name" "$SERVICES_FILE")

    # Substitute {{service}} in the policy template, write policy.
    POLICY_NAME="svc-$SVC"
    POLICY_TMP=$(mktemp)
    sed "s/{{service}}/$SVC/g" "$POLICY_TMPL" > "$POLICY_TMP"
    vault policy write "$POLICY_NAME" "$POLICY_TMP" >/dev/null
    rm "$POLICY_TMP"
    log "Wrote policy $POLICY_NAME"

    # Substitute {{service}} in the AppRole template, configure role.
    APPROLE_NAME="haworks-$SVC"
    APPROLE_TTL=$(jq -r '.token_ttl' "$APPROLE_TMPL")
    APPROLE_MAX_TTL=$(jq -r '.token_max_ttl' "$APPROLE_TMPL")
    APPROLE_SECRET_TTL=$(jq -r '.secret_id_ttl' "$APPROLE_TMPL")
    APPROLE_SECRET_NUM=$(jq -r '.secret_id_num_uses' "$APPROLE_TMPL")

    vault write "auth/approle/role/$APPROLE_NAME" \
        token_policies="$POLICY_NAME" \
        token_ttl="$APPROLE_TTL" \
        token_max_ttl="$APPROLE_MAX_TTL" \
        secret_id_ttl="$APPROLE_SECRET_TTL" \
        secret_id_num_uses="$APPROLE_SECRET_NUM" >/dev/null
    log "Configured AppRole $APPROLE_NAME -> $POLICY_NAME"

    # Issue role_id + secret_id; write to /creds/<service>/{role_id,secret_id}.
    ROLE_ID=$(vault read -field=role_id "auth/approle/role/$APPROLE_NAME/role-id")
    SECRET_ID=$(vault write -force -field=secret_id "auth/approle/role/$APPROLE_NAME/secret-id")

    mkdir -p "/creds/$SVC"
    printf "%s" "$ROLE_ID"   > "/creds/$SVC/role_id"
    printf "%s" "$SECRET_ID" > "/creds/$SVC/secret_id"
    chmod 644 "/creds/$SVC/role_id" "/creds/$SVC/secret_id"
    log "Wrote AppRole creds for $SVC to /creds/$SVC/"

    i=$((i + 1))
done

# --- KV v2 secrets (per-service paths from kv-layout.json) ---------------
KV_LAYOUT="$MANIFESTS/secrets/kv-layout.json"
KV_VALUES="$MANIFESTS/secrets/kv-dev-values.json"
KV_MOUNT=$(jq -r '.mount' "$KV_LAYOUT")

PATHS=$(jq -r '.paths | keys[]' "$KV_LAYOUT")
for kv_path in $PATHS; do
    args=$(jq -r --arg p "$kv_path" '.[$p] | to_entries[] | "\(.key)=\(.value)"' "$KV_VALUES")
    if [ -z "$args" ]; then
        echo "[vault-init] FATAL: $KV_VALUES has no values for path '$kv_path'" >&2
        exit 1
    fi
    # shellcheck disable=SC2086
    vault kv put "$KV_MOUNT/$kv_path" $args >/dev/null
    log "Seeded KV $KV_MOUNT/$kv_path"
done

# --- Database secrets engine ---------------------------------------------
if ! vault secrets list 2>/dev/null | grep -q '^database/'; then
    vault secrets enable database
    log "Enabled database secrets engine"
fi

# Aspire-injected connection string format:
# Host=postgres;Port=5432;Username=postgres;Password=<rand>
PG_HOST=$(echo "${ConnectionStrings__postgres:-}" | tr ';' '\n' | grep '^Host='     | cut -d= -f2-)
PG_PORT=$(echo "${ConnectionStrings__postgres:-}" | tr ';' '\n' | grep '^Port='     | cut -d= -f2-)
PG_USER=$(echo "${ConnectionStrings__postgres:-}" | tr ';' '\n' | grep '^Username=' | cut -d= -f2-)
PG_PASS=$(echo "${ConnectionStrings__postgres:-}" | tr ';' '\n' | grep '^Password=' | cut -d= -f2-)

if [ -z "$PG_HOST" ] || [ -z "$PG_USER" ] || [ -z "$PG_PASS" ]; then
    echo "[vault-init] FATAL: ConnectionStrings__postgres is empty or unparseable" >&2
    echo "[vault-init] value=${ConnectionStrings__postgres:-<unset>}" >&2
    exit 1
fi
PG_PORT="${PG_PORT:-5432}"

DB_ROLES="$MANIFESTS/database/roles.json"
DB_CONN_NAME=$(jq -r '.connection_name' "$DB_ROLES")
DB_PLUGIN=$(jq -r '.plugin_name' "$DB_ROLES")
DB_ROTATION_PERIOD=$(jq -r '.rotation_period' "$DB_ROLES")
DB_ALLOWED_ROLES=$(jq -r '.roles | map(.role_name) | join(",")' "$DB_ROLES")

log "Configuring postgres connection at $PG_HOST:$PG_PORT (user=$PG_USER)"
vault write "database/config/$DB_CONN_NAME" \
    plugin_name="$DB_PLUGIN" \
    allowed_roles="$DB_ALLOWED_ROLES" \
    connection_url="postgresql://{{username}}:{{password}}@$PG_HOST:$PG_PORT/postgres?sslmode=disable" \
    username="$PG_USER" \
    password="$PG_PASS" >/dev/null

# Per-bounded-context static roles.
ROLE_COUNT=$(jq -r '.roles | length' "$DB_ROLES")
i=0
while [ "$i" -lt "$ROLE_COUNT" ]; do
    ROLE_NAME=$(jq -r ".roles[$i].role_name"  "$DB_ROLES")
    USERNAME=$(jq    -r ".roles[$i].username" "$DB_ROLES")

    vault write "database/static-roles/$ROLE_NAME" \
        db_name="$DB_CONN_NAME" \
        rotation_period="$DB_ROTATION_PERIOD" \
        username="$USERNAME" >/dev/null
    log "Configured static role $ROLE_NAME -> user $USERNAME"

    i=$((i + 1))
done

# Sentinel — re-runs short-circuit at the top of the script. Bumped only
# when this script's contract changes (new policies, new AppRoles, …).
printf "%s" "$SENTINEL_VERSION" > "$SENTINEL"
log "Wrote sentinel $SENTINEL ($SENTINEL_VERSION)"

log "DONE"
