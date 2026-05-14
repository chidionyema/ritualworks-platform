#!/usr/bin/env bash
set -euo pipefail

CDC_URL="${CDC_URL:-http://localhost:5106/api/cdc}"

usage() {
    echo "cdc — CLI for Change Data Capture service"
    echo "Usage: cdc <command> [args]"
    echo ""
    echo "Commands:"
    echo "  status                  show all sources and their states"
    echo "  pause <service>         disable a source"
    echo "  resume <service>        enable a source"
    echo "  add <service> <conn> <slot>  register a new source"
}

cmd_status() {
    curl -s "$CDC_URL/status" | jq -r '
        ["SERVICE", "ENABLED", "RUNNING", "SLOT"],
        ["-------", "-------", "-------", "----"],
        (.[] | [.serviceName, .enabled, .isRunning, .slotName]) | @tsv' | column -t
}

cmd_pause() {
    local svc=$1
    curl -s -X POST "$CDC_URL/sources/$svc/pause" && echo "Paused $svc"
}

cmd_resume() {
    local svc=$1
    curl -s -X POST "$CDC_URL/sources/$svc/resume" && echo "Resumed $svc"
}

cmd_add() {
    local svc=$1 conn=$2 slot=$3
    curl -s -X POST "$CDC_URL/sources" -H "Content-Type: application/json" \
        -d "{\"serviceName\": \"$svc\", \"connectionString\": \"$conn\", \"slotName\": \"$slot\"}"
}

cmd=${1:-help}
case "$cmd" in
    status) cmd_status ;;
    pause)  cmd_pause "${2:-}" ;;
    resume) cmd_resume "${2:-}" ;;
    add)    cmd_add "${2:-}" "${3:-}" "${4:-}" ;;
    *)      usage ;;
esac
