#!/usr/bin/env bash
set -euo pipefail

CONNECT_URL="${CONNECT_URL:-http://localhost:8083}"

usage() {
    echo "cdc — CLI for Debezium Connect CDC pipeline"
    echo "Usage: cdc <command> [args]"
    echo ""
    echo "Commands:"
    echo "  status                      show all connectors and their states"
    echo "  pause <connector>           pause a connector"
    echo "  resume <connector>          resume a connector"
    echo "  register <connector.json>   register or update a connector from JSON file"
    echo "  delete <connector>          delete a connector"
    echo "  topics                      list Kafka topics via connector offsets"
}

cmd_status() {
    local connectors
    connectors=$(curl -sf "$CONNECT_URL/connectors?expand=status" 2>/dev/null) || {
        echo "ERROR: Cannot reach Debezium Connect at $CONNECT_URL" >&2
        exit 1
    }
    echo "$connectors" | jq -r '
        to_entries[] |
        [.key, .value.status.connector.state, .value.status.connector.worker_id,
         (.value.status.tasks | length | tostring),
         (.value.status.tasks | map(.state) | join(","))] |
        @tsv' | column -t -N "CONNECTOR,STATE,WORKER,TASKS,TASK_STATES"
}

cmd_pause() {
    local name=$1
    curl -sf -X PUT "$CONNECT_URL/connectors/$name/pause" && echo "Paused $name"
}

cmd_resume() {
    local name=$1
    curl -sf -X PUT "$CONNECT_URL/connectors/$name/resume" && echo "Resumed $name"
}

cmd_register() {
    local file=$1
    local name
    name=$(basename "$file" .json | sed 's/-connector$//')
    echo "Registering connector: $name"
    curl -sf -X PUT "$CONNECT_URL/connectors/$name/config" \
        -H 'Content-Type: application/json' \
        -d @"$file" | jq .
}

cmd_delete() {
    local name=$1
    curl -sf -X DELETE "$CONNECT_URL/connectors/$name" && echo "Deleted $name"
}

cmd_topics() {
    curl -sf "$CONNECT_URL/connectors?expand=info" | jq -r '
        to_entries[] |
        .value.info.config |
        [.name // "unknown", ."topic.prefix" // "unknown", ."database.dbname" // "unknown"] |
        @tsv' | column -t -N "CONNECTOR,TOPIC_PREFIX,DATABASE"
}

cmd=${1:-help}
case "$cmd" in
    status)   cmd_status ;;
    pause)    cmd_pause "${2:?connector name required}" ;;
    resume)   cmd_resume "${2:?connector name required}" ;;
    register) cmd_register "${2:?path to connector JSON required}" ;;
    delete)   cmd_delete "${2:?connector name required}" ;;
    topics)   cmd_topics ;;
    *)        usage ;;
esac
