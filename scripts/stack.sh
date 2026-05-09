#!/usr/bin/env bash
# =============================================================================
# stack.sh — single entrypoint for the local dev stack.
#
# Both docker-compose AND Aspire are first-class daily tools. They cannot
# run at the same time — each wants the canonical ports (5432, 5672, 8200,
# 4566, 7700, 6379, 3310, 4317). This script makes switching between them
# safe and fast: it always stops the other side before starting yours.
#
#   ./scripts/stack.sh up              # bring up docker-compose stack
#   ./scripts/stack.sh aspire          # bring up Aspire AppHost (dashboard)
#   ./scripts/stack.sh down            # stop everything (both modes)
#   ./scripts/stack.sh status          # show what's running
#   ./scripts/stack.sh rebuild <svc>   # rebuild + restart a single compose service
#   ./scripts/stack.sh logs <svc>      # follow logs for a compose service
#   ./scripts/stack.sh prebuild        # warm caches: dotnet build + image pulls
#
# When in doubt, `./scripts/stack.sh down && ./scripts/stack.sh up` returns
# to a known-good state in ~10s warm.
# =============================================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/deploy/compose/docker-compose.yml"
ASPIRE_PROJECT="$REPO_ROOT/deploy/aspire/RitualworksPlatform.AppHost.csproj"
ASPIRE_PIDFILE="/tmp/rw-aspire.pid"
COMPOSE="docker compose -f $COMPOSE_FILE"

log()  { printf "\033[1;36m[stack]\033[0m %s\n" "$*"; }
warn() { printf "\033[1;33m[stack]\033[0m %s\n" "$*" >&2; }
die()  { printf "\033[1;31m[stack]\033[0m %s\n" "$*" >&2; exit 1; }

# Aspire publishes containers with a random suffix (e.g. `postgres-abc123`).
# When ContainerLifetime.Persistent is used, those survive across runs and
# claim the host ports. List + stop them so compose can take over cleanly.
stop_aspire_containers() {
    local aspire_containers
    aspire_containers="$(docker ps --format '{{.Names}}' \
        | grep -E '^(postgres|redis|rabbitmq|vault|localstack|meilisearch|clamav|tempo|pact-db|pact-broker)-[a-z0-9]+$' \
        || true)"
    if [ -n "$aspire_containers" ]; then
        log "Stopping Aspire-named containers (host-port owners): $(echo $aspire_containers | tr '\n' ' ')"
        echo "$aspire_containers" | xargs -r docker stop >/dev/null
    fi

    if [ -f "$ASPIRE_PIDFILE" ]; then
        local pid; pid="$(cat "$ASPIRE_PIDFILE" 2>/dev/null || true)"
        if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
            log "Stopping Aspire AppHost process pid=$pid"
            kill "$pid" 2>/dev/null || true
            sleep 1
        fi
        rm -f "$ASPIRE_PIDFILE"
    fi
}

stop_compose() {
    if $COMPOSE ps -q 2>/dev/null | grep -q .; then
        log "Stopping compose stack"
        $COMPOSE down
    fi
}

cmd_up() {
    stop_aspire_containers
    log "Bringing up compose stack"
    $COMPOSE up -d
    log "Compose stack up. BFF: http://localhost:5050  Vault: http://localhost:8200"
}

cmd_aspire() {
    stop_compose
    log "Pre-building solution + pulling images so Aspire boots fast"
    (cd "$REPO_ROOT" && dotnet build RitualworksPlatform.sln -c Release --nologo --verbosity quiet) &
    local build_pid=$!
    docker pull -q postgres:16-alpine               &
    docker pull -q rabbitmq:3.13-management-alpine  &
    docker pull -q hashicorp/vault:1.15             &
    docker pull -q localstack/localstack:3          &
    docker pull -q getmeili/meilisearch:v1.10       &
    docker pull -q redis:7-alpine                   &
    docker pull -q grafana/tempo:latest             &
    wait $build_pid
    wait
    log "Booting Aspire AppHost (dashboard URL printed below)"
    cd "$REPO_ROOT"
    nohup dotnet run --project "$ASPIRE_PROJECT" -c Release --no-build \
        > /tmp/rw-aspire.log 2>&1 &
    echo $! > "$ASPIRE_PIDFILE"
    log "Aspire pid $(cat "$ASPIRE_PIDFILE"). Tail logs: ./scripts/stack.sh aspire-logs"
}

cmd_aspire_logs() {
    [ -f /tmp/rw-aspire.log ] || die "Aspire log /tmp/rw-aspire.log not found — run './scripts/stack.sh aspire' first"
    tail -F /tmp/rw-aspire.log
}

cmd_down() {
    stop_compose
    stop_aspire_containers
    log "All stacks down."
}

cmd_status() {
    printf "\n\033[1mCompose containers:\033[0m\n"
    docker ps --filter "name=^rw-" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}' || true
    printf "\n\033[1mAspire-named containers:\033[0m\n"
    docker ps --format '{{.Names}}\t{{.Status}}' \
        | grep -E '^(postgres|redis|rabbitmq|vault|localstack|meilisearch|clamav|tempo|pact-db|pact-broker)-[a-z0-9]+' \
        || echo "  (none)"
    if [ -f "$ASPIRE_PIDFILE" ] && kill -0 "$(cat "$ASPIRE_PIDFILE" 2>/dev/null)" 2>/dev/null; then
        printf "\n\033[1mAspire AppHost:\033[0m running (pid $(cat "$ASPIRE_PIDFILE"))\n"
    fi
    echo
}

cmd_rebuild() {
    local svc="${1:-}"
    [ -n "$svc" ] || die "rebuild needs a service name (e.g. content-svc)"
    log "Rebuilding $svc (no-cache, then restart)"
    $COMPOSE build --no-cache "$svc"
    $COMPOSE up -d --no-build "$svc"
    log "$svc rebuilt + restarted"
}

cmd_logs() {
    local svc="${1:-}"
    [ -n "$svc" ] || die "logs needs a service name (e.g. content-svc)"
    $COMPOSE logs -f "$svc"
}

cmd_prebuild() {
    log "Pre-building solution + pulling images (used by both compose and aspire)"
    (cd "$REPO_ROOT" && dotnet build RitualworksPlatform.sln -c Release --nologo --verbosity quiet) &
    local build_pid=$!
    docker pull -q postgres:16-alpine               &
    docker pull -q rabbitmq:3.13-management-alpine  &
    docker pull -q hashicorp/vault:1.15             &
    docker pull -q localstack/localstack:3          &
    docker pull -q getmeili/meilisearch:v1.10       &
    docker pull -q redis:7-alpine                   &
    docker pull -q grafana/tempo:latest             &
    wait $build_pid
    wait
    log "Pre-build complete."
}

cmd_help() {
    sed -n '2,18p' "$0"
}

case "${1:-help}" in
    up)         cmd_up ;;
    aspire)     cmd_aspire ;;
    aspire-logs) cmd_aspire_logs ;;
    down)       cmd_down ;;
    status)     cmd_status ;;
    rebuild)    shift; cmd_rebuild "$@" ;;
    logs)       shift; cmd_logs "$@" ;;
    prebuild)   cmd_prebuild ;;
    help|-h|--help) cmd_help ;;
    *)          warn "Unknown command: $1"; cmd_help; exit 1 ;;
esac
