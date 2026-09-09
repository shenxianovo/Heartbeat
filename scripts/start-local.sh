#!/usr/bin/env bash

set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/start-local.sh [options]

Build and start the local end-to-end stack (Postgres + backend + frontend + headless Hub).
Uses the existing local database; never downloads or restores server data.
The backend applies pending database migrations on startup.

Options:
  --desktop           Also run the macOS development Desktop in the foreground
  --desktop-only      Only run Desktop against the existing local stack (no Docker required)
  --compose-file PATH  Compose file (default: compose.local.yml)
  --env-file PATH      Environment file (default: .env.local)
  --wait-timeout SEC   Startup/migration wait budget (default: 1800 seconds)
  -h, --help           Show this help

Desktop opens its settings window, using http://localhost:8080 and this checkout's .local/desktop Profile.
Exit Desktop from its menu to stop it; Compose services remain running.
EOF
}

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository_root=$(cd -- "$script_directory/.." && pwd -P)
compose_file="$repository_root/compose.local.yml"
env_file="$repository_root/.env.local"
desktop=false
desktop_only=false
wait_timeout=1800

while (($# > 0)); do
    case "$1" in
        --desktop)
            desktop=true
            shift
            ;;
        --desktop-only)
            desktop=true
            desktop_only=true
            shift
            ;;
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
        --wait-timeout)
            [[ $# -ge 2 && "$2" =~ ^[1-9][0-9]*$ ]] || { echo 'A positive --wait-timeout is required.' >&2; exit 2; }
            wait_timeout=$2
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

if [[ "$desktop" == true ]]; then
    [[ "$(uname -s)" == Darwin ]] || {
        echo 'Desktop requires macOS with this script; on Windows use start-local.ps1 -Desktop.' >&2
        exit 2
    }
    command -v dotnet >/dev/null 2>&1 || { echo '.NET 10 SDK is required for Desktop.' >&2; exit 1; }
fi

start_desktop() {
    echo "Starting development Desktop: $repository_root/.local/desktop -> http://localhost:8080"
    echo 'Exit Desktop from its menu to stop it; Compose services remain running.'
    export HEARTBEAT_API_BASE_URL=http://localhost:8080
    export HEARTBEAT_SHOW_SETTINGS_ON_START=1
    exec dotnet run --project "$repository_root/collection/desktop/Heartbeat.Desktop.Mac/Heartbeat.Desktop.Mac.csproj" \
        -- --data-directory "$repository_root/.local/desktop"
}

if [[ "$desktop_only" == true ]]; then
    start_desktop
fi

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

echo '[2/3] Building and starting the local stack (backend applies pending database migrations)...'
"${compose[@]}" up --build --detach

echo '[3/3] Waiting for Analytics and the Headless Hub...'
bash "$script_directory/wait-for-stack.sh" --compose-file "$compose_file" --env-file "$env_file" \
    --timeout-seconds "$wait_timeout" --hub-url http://127.0.0.1:8080/hub/api/v1/collectors

echo 'Local stack ready: http://localhost:8080'
if [[ "$desktop" == true ]]; then
    start_desktop
fi
