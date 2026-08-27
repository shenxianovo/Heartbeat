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
docker info >/dev/null 2>&1 || {
    echo 'Docker is not ready. Start Docker Desktop and wait for the engine to finish starting.' >&2
    exit 1
}

compose=(docker compose --file "$compose_file" --env-file "$env_file")

echo '[1/2] Building and starting the local stack...'
"${compose[@]}" up --build --detach

echo '[2/2] Waiting for http://localhost:8080...'
ready=false
for ((attempt = 1; attempt <= 60; attempt++)); do
    status_code=$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
        http://127.0.0.1:8080/ || true)
    if [[ "$status_code" =~ ^[234][0-9][0-9]$ ]]; then
        ready=true
        break
    fi
    sleep 1
done

if [[ "$ready" != true ]]; then
    echo 'http://localhost:8080 did not become ready within 60 seconds.' >&2
    echo "Check: docker compose --file '$compose_file' --env-file '$env_file' logs" >&2
    exit 1
fi

if ! "${compose[@]}" ps --status running --services headless | grep -qx 'headless'; then
    echo 'The headless Hub did not remain running.' >&2
    echo "Check: docker compose --file '$compose_file' --env-file '$env_file' logs headless" >&2
    exit 1
fi

echo 'Local stack ready: http://localhost:8080'
