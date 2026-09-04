#!/usr/bin/env bash

set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/start-local.sh [options]

Build and start the local end-to-end stack (Postgres + backend + frontend + headless Hub).

Options:
  --compose-file PATH  Compose file (default: compose.local.yml)
  --env-file PATH      Environment file (default: .env.local)
  -h, --help           Show this help
EOF
}

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository_root=$(cd -- "$script_directory/.." && pwd -P)
compose_file="$repository_root/compose.local.yml"
env_file="$repository_root/.env.local"

while (($# > 0)); do
    case "$1" in
        --compose-file)
            [[ $# -ge 2 ]] || { echo 'Missing value for --compose-file.' >&2; exit 2; }
            compose_file=$2
            shift 2
            ;;
        --env-file)
            [[ $# -ge 2 ]] || { echo 'Missing value for --env-file.' >&2; exit 2; }
            env_file=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

[[ -f "$compose_file" ]] || { echo "Compose file not found: $compose_file" >&2; exit 1; }
[[ -f "$env_file" ]] || {
    echo ".env.local not found. Run: cp .env.local.example .env.local" >&2
    exit 1
}
command -v docker >/dev/null 2>&1 || { echo 'docker is required.' >&2; exit 1; }
command -v curl >/dev/null 2>&1 || { echo 'curl is required.' >&2; exit 1; }
docker compose version >/dev/null 2>&1 || {
    echo 'Docker Compose v2 is required (the "docker compose" command).' >&2
    exit 1
}
docker info >/dev/null 2>&1 || {
    echo 'Docker is not ready. Start Docker Desktop and wait for the engine to finish starting.' >&2
    exit 1
}

compose=(docker compose --file "$compose_file" --env-file "$env_file")

echo '[1/3] Validating the local stack configuration...'
"${compose[@]}" config --quiet

echo '[2/3] Building and starting the local stack...'
"${compose[@]}" up --build --detach

echo '[3/3] Waiting for Analytics and the Headless Hub...'
analytics_ready=false
hub_ready=false
analytics_status=000
hub_status=000
for ((attempt = 1; attempt <= 60; attempt++)); do
    if [[ "$analytics_ready" != true ]]; then
        analytics_status=$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
            http://127.0.0.1:8080/health || true)
        [[ "$analytics_status" == 200 ]] && analytics_ready=true
    fi

    if [[ "$hub_ready" != true ]]; then
        hub_status=$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
            http://127.0.0.1:8080/hub/api/v1/collectors || true)
        [[ "$hub_status" == 401 || "$hub_status" == 403 ]] && hub_ready=true
    fi

    [[ "$analytics_ready" == true && "$hub_ready" == true ]] && break
    sleep 1
done

if [[ "$analytics_ready" != true || "$hub_ready" != true ]]; then
    echo "The local stack did not become ready within 60 seconds (Analytics: $analytics_status, Headless Hub: $hub_status)." >&2
    echo "Check: docker compose --file '$compose_file' --env-file '$env_file' logs" >&2
    exit 1
fi

echo 'Local stack ready: http://localhost:8080'
