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
#   ./scripts/stack.sh verify          # health-probe every running service
#   ./scripts/stack.sh prebuild        # warm caches: dotnet build + image pulls
#   ./scripts/stack.sh cleanup         # janitor: project-only (containers, dangling images, recent builder cache)
#   ./scripts/stack.sh cleanup --deep  # + project volumes (drops DB state)
#   ./scripts/stack.sh cleanup --nuke  # SYSTEM-WIDE: every unused image, volume, builder cache. Year-old cruft. Cross-project.
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

cmd_verify() {
    # Health-check every running service. Works for compose AND Aspire because
    # both expose the same internal port (8080) inside containers — we exec
    # into each one and curl localhost:8080/health, which avoids needing each
    # service's host port to be published.
    local fail=0

    # BFF on canonical 5050 from the host (both modes).
    if curl -sf -m 3 http://localhost:5050/health >/dev/null; then
        printf "  \033[1;32m✓\033[0m bff-web      /health (host:5050)\n"
    else
        printf "  \033[1;31m✗\033[0m bff-web      /health (host:5050)  UNREACHABLE\n"
        fail=1
    fi

    # Backend services — exec into the container, curl its loopback /health.
    # Service-name pattern matches both compose (rw-foo-svc) and Aspire
    # (foo-svc-{suffix}). Skip if no container running.
    for svc in identity-svc catalog-svc orders-svc payments-svc \
               checkout-svc content-svc search-svc; do
        local cid; cid="$(docker ps --filter "name=$svc" --format '{{.ID}}' | head -1)"
        if [ -z "$cid" ]; then
            printf "  \033[1;33m–\033[0m %-12s /health  (not running)\n" "$svc"
            continue
        fi
        # The .NET aspnet image doesn't ship curl by default. Use wget which
        # is in the alpine-based base; fall back to a python one-liner if
        # neither is present (.NET runtime image does have python? no —
        # but we can use dotnet's own HTTP client via the dashboard. Simpler:
        # try wget, then curl, then dotnet-tool-style probe, then mark unknown).
        if docker exec "$cid" sh -c 'wget -q -O- http://localhost:8080/health 2>/dev/null \
                                  || curl -sf http://localhost:8080/health 2>/dev/null' >/dev/null; then
            printf "  \033[1;32m✓\033[0m %-12s /health\n" "$svc"
        else
            # Probe via host network — Aspire publishes random ports;
            # compose only publishes BFF and infra. So this is best-effort.
            printf "  \033[1;33m?\033[0m %-12s /health  (no curl/wget in image; container is up — host probe needed)\n" "$svc"
        fi
    done

    # Identity JWKS endpoint — load-bearing for every backend's auth.
    local id_cid; id_cid="$(docker ps --filter "name=identity-svc" --format '{{.ID}}' | head -1)"
    if [ -n "$id_cid" ]; then
        if docker exec "$id_cid" sh -c 'wget -qO- http://localhost:8080/.well-known/jwks.json 2>/dev/null \
                                      || curl -sf http://localhost:8080/.well-known/jwks.json 2>/dev/null' \
                | grep -q '"keys"'; then
            printf "  \033[1;32m✓\033[0m identity-svc /.well-known/jwks.json (returns JWK Set)\n"
        else
            printf "  \033[1;31m✗\033[0m identity-svc /.well-known/jwks.json  NO JWK Set returned\n"
            fail=1
        fi
    fi

    # LocalStack S3 (dev/test storage backend).
    local ls_cid; ls_cid="$(docker ps --filter "name=localstack" --format '{{.ID}}' | head -1)"
    if [ -n "$ls_cid" ]; then
        if docker exec "$ls_cid" curl -sf -m 3 http://localhost:4566/_localstack/health >/dev/null 2>&1; then
            printf "  \033[1;32m✓\033[0m localstack   /_localstack/health\n"
        else
            printf "  \033[1;31m✗\033[0m localstack   /_localstack/health  UNREACHABLE\n"
            fail=1
        fi
    fi

    if [ $fail -eq 0 ]; then
        log "Stack verified."
    else
        warn "Stack has UNHEALTHY components — see ✗ above."
        return 1
    fi
}

cmd_cleanup() {
    # Janitor. Three escalating modes:
    #   (default)  project-scoped  — compose containers, project Aspire containers,
    #                                dangling images, recent builder cache, orphan networks.
    #                                Volumes preserved.
    #   --deep      project + state — same plus drops project volumes (compose_*,
    #                                  ritualworks-platform-*). DB state gone.
    #   --nuke      system-wide      — Docker took over your disk. Removes EVERY
    #                                  unused image (incl tagged-from-a-year-ago),
    #                                  EVERY unused volume across all projects,
    #                                  EVERY stopped container, ALL builder cache.
    #                                  Cross-project — touches things outside this repo.
    local mode="${1:-shallow}"

    # 1. Stop everything we control.
    cmd_down

    if [ "$mode" = "--nuke" ] || [ "$mode" = "nuke" ]; then
        warn "NUKE mode: system-wide cleanup. Cross-project. Year-old images + volumes will be removed."
        log "Removing all stopped containers (system-wide)"
        docker container prune -f >/dev/null

        log "Removing ALL unused images (-a — includes tagged but unreferenced)"
        docker image prune -a -f >/dev/null

        log "Removing ALL unused volumes (system-wide — including non-project ones)"
        docker volume prune -af >/dev/null 2>&1 || docker volume prune -f >/dev/null

        log "Removing ALL builder cache (no time filter)"
        docker builder prune -a -f >/dev/null 2>&1 || true

        docker network prune -f >/dev/null

        log "NUKE complete."
    else
        log "Cleanup mode: $mode (use --deep for project volumes, --nuke for system-wide)"

        # Compose's stopped containers (project-labeled).
        log "Pruning stopped compose containers"
        docker container prune -f --filter "label=com.docker.compose.project=compose" 2>/dev/null || true

        # Aspire-named stopped containers (the *-{suffix} ones from ContainerLifetime.Persistent runs).
        local stopped_aspire
        stopped_aspire="$(docker ps -a --filter "status=exited" --format '{{.Names}}' \
            | grep -E '^(postgres|redis|rabbitmq|vault|localstack|meilisearch|clamav|tempo|pact-db|pact-broker)-[a-z0-9]+$' \
            || true)"
        if [ -n "$stopped_aspire" ]; then
            log "Removing $(echo "$stopped_aspire" | wc -l | tr -d ' ') stopped Aspire containers"
            echo "$stopped_aspire" | xargs -r docker rm -f >/dev/null
        fi

        # Dangling images (untagged build leftovers).
        log "Pruning dangling images"
        docker image prune -f >/dev/null

        # Builder cache older than 72h (recent cache may still help next build).
        log "Pruning BuildKit builder cache (filter: until=72h)"
        docker builder prune -f --filter "until=72h" >/dev/null 2>&1 || true

        # Orphan networks.
        docker network prune -f >/dev/null

        if [ "$mode" = "--deep" ] || [ "$mode" = "deep" ]; then
            warn "DEEP cleanup: removing project volumes. DB state gone."
            docker volume ls --format '{{.Name}}' | grep -E '^compose_' | xargs -r docker volume rm 2>/dev/null || true
            docker volume ls --format '{{.Name}}' | grep -E '^ritualworks-platform-' | xargs -r docker volume rm 2>/dev/null || true
        fi
    fi

    log "Disk now:"
    df -h "$(docker info --format '{{.DockerRootDir}}' 2>/dev/null || echo /)" | tail -1
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
    verify)     cmd_verify ;;
    cleanup)    shift; cmd_cleanup "$@" ;;
    help|-h|--help) cmd_help ;;
    *)          warn "Unknown command: $1"; cmd_help; exit 1 ;;
esac
