#!/usr/bin/env bash

set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/refresh-local-data.sh [options]

Replace the local E2E PostgreSQL database with a transaction-consistent server snapshot.
The server is read only; only .local/postgres-data in this checkout is replaced.

Options:
  --ssh-destination HOST       SSH destination, for example user@example.com
  --remote-directory PATH      Remote project directory (default: /srv/heartbeat)
  --remote-dir PATH            Alias for --remote-directory
  --remote-compose-file PATH   Remote Compose file (default: compose.yml)
  --remote-env-file PATH       Remote environment file (default: .env)
  --ssh-port PORT              SSH port (default: 22)
  --identity-file PATH         SSH private key
  --compose-file PATH          Local Compose file (default: compose.local.yml)
  --env-file PATH              Local environment file (default: .env.local)
  --keep-dump                  Keep the downloaded sensitive dump
  --force                      Skip the destructive local-data confirmation
  -h, --help                   Show this help
EOF
}

require_option_value() {
    if [[ $# -lt 2 ]]; then
        echo "Missing value for $1." >&2
        exit 2
    fi
}

resolve_file() {
    local path=$1
    local description=$2
    [[ -f "$path" ]] || { echo "$description not found: $path" >&2; exit 1; }
    local directory
    directory=$(cd -- "$(dirname -- "$path")" && pwd -P)
    printf '%s/%s\n' "$directory" "$(basename -- "$path")"
}

quote_posix_shell_argument() {
    local value=$1
    value=${value//\'/\'\\\'\'}
    printf "'%s'" "$value"
}

test_local_database_ready() {
    local result
    result=$("${compose[@]}" exec -T db psql \
        --username=heartbeat --dbname=heartbeat --tuples-only --no-align \
        --command 'SELECT 1' 2>/dev/null) || return 1
    [[ "${result//[[:space:]]/}" == '1' ]]
}

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository_root=$(cd -- "$script_directory/.." && pwd -P)
ssh_destination=''
remote_directory='/srv/heartbeat'
remote_directory_was_set=false
remote_compose_file='compose.yml'
remote_env_file='.env'
ssh_port=22
identity_file=''
compose_file="$repository_root/compose.local.yml"
env_file="$repository_root/.env.local"
keep_dump=false
force=false

while (($# > 0)); do
    case "$1" in
        --ssh-destination)
            require_option_value "$@"
            ssh_destination=$2
            shift 2
            ;;
        --remote-directory|--remote-dir)
            require_option_value "$@"
            remote_directory=$2
            remote_directory_was_set=true
            shift 2
            ;;
        --remote-compose-file)
            require_option_value "$@"
            remote_compose_file=$2
            shift 2
            ;;
        --remote-env-file)
            require_option_value "$@"
            remote_env_file=$2
            shift 2
            ;;
        --ssh-port)
            require_option_value "$@"
            ssh_port=$2
            shift 2
            ;;
        --identity-file)
            require_option_value "$@"
            identity_file=$2
            shift 2
            ;;
        --compose-file)
            require_option_value "$@"
            compose_file=$2
            shift 2
            ;;
        --env-file)
            require_option_value "$@"
            env_file=$2
            shift 2
            ;;
        --keep-dump)
            keep_dump=true
            shift
            ;;
        --force)
            force=true
            shift
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

if [[ -z "$ssh_destination" ]]; then
    read -r -p 'SSH destination (for example user@example.com): ' ssh_destination
    [[ -n "$ssh_destination" ]] || { echo 'SSH destination is required.' >&2; exit 1; }
fi

if [[ "$remote_directory_was_set" != true ]]; then
    read -r -p "Remote directory [$remote_directory]: " entered_remote_directory
    if [[ -n "$entered_remote_directory" ]]; then
        remote_directory=$entered_remote_directory
    fi
fi

[[ "$ssh_port" =~ ^[0-9]+$ ]] && ((ssh_port >= 1 && ssh_port <= 65535)) || {
    echo "SSH port must be between 1 and 65535: $ssh_port" >&2
    exit 2
}

compose_file=$(resolve_file "$compose_file" 'Local Compose file')
env_file=$(resolve_file "$env_file" 'Local environment file')
if [[ -n "$identity_file" ]]; then
    identity_file=$(resolve_file "$identity_file" 'SSH identity file')
fi

for command_name in docker ssh curl mktemp; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "$command_name is required." >&2
        exit 1
    }
done
docker info >/dev/null 2>&1 || {
    echo 'Docker is not ready. Start Docker Desktop and wait for the engine to finish starting.' >&2
    exit 1
}

compose=(docker compose --file "$compose_file" --env-file "$env_file")
"${compose[@]}" config --quiet

local_data_root="$repository_root/.local"
local_database_directory="$local_data_root/postgres-data"
case "$local_database_directory" in
    "$local_data_root"/*) ;;
    *)
        echo "Refusing to manage a database directory outside $local_data_root" >&2
        exit 1
        ;;
esac

if [[ "$force" != true ]]; then
    echo "WARNING: This replaces $local_database_directory with a snapshot containing private server data." >&2
    read -r -p 'Type REPLACE to continue: ' confirmation
    if [[ "$confirmation" != 'REPLACE' ]]; then
        echo 'Cancelled; no data was changed.'
        exit 0
    fi
fi

dump_path=$(mktemp "${TMPDIR:-/tmp}/heartbeat-server.XXXXXX")
local_migrations_path=$(mktemp "${TMPDIR:-/tmp}/heartbeat-migrations.XXXXXX")
container_dump_path='/tmp/heartbeat-server.dump'
dump_copied_to_container=false

cleanup() {
    local exit_code=$?
    trap - EXIT
    if [[ "$dump_copied_to_container" == true ]]; then
        "${compose[@]}" exec -T db rm -f "$container_dump_path" >/dev/null 2>&1 || true
    fi
    rm -f -- "$local_migrations_path"
    if [[ "$keep_dump" == true ]]; then
        if [[ -f "$dump_path" ]]; then
            echo "WARNING: Sensitive server dump retained at: $dump_path" >&2
        fi
    else
        rm -f -- "$dump_path"
    fi
    exit "$exit_code"
}
trap cleanup EXIT

quoted_directory=$(quote_posix_shell_argument "$remote_directory")
quoted_compose_file=$(quote_posix_shell_argument "$remote_compose_file")
quoted_env_file=$(quote_posix_shell_argument "$remote_env_file")
remote_command="set -eu; cd -- $quoted_directory; docker compose --file $quoted_compose_file --env-file $quoted_env_file exec -T db pg_dump --username=heartbeat --dbname=heartbeat --format=custom --compress=6 --no-owner --no-privileges"

ssh_arguments=(-o BatchMode=no -o NumberOfPasswordPrompts=3 -p "$ssh_port")
if [[ -n "$identity_file" ]]; then
    ssh_arguments+=(-i "$identity_file")
fi

echo '[1/6] Streaming a transaction-consistent server snapshot over SSH...'
ssh "${ssh_arguments[@]}" "$ssh_destination" "$remote_command" >"$dump_path"

if [[ $(LC_ALL=C dd if="$dump_path" bs=5 count=1 2>/dev/null) != 'PGDMP' ]]; then
    echo 'The downloaded file is not a PostgreSQL custom-format dump.' >&2
    exit 1
fi
size_mib=$(du -m "$dump_path" | awk '{print $1}')
echo "      Downloaded ${size_mib} MiB."

echo '[2/6] Recreating the project-local database directory...'
"${compose[@]}" down --remove-orphans
if [[ -e "$local_database_directory" ]]; then
    rm -rf -- "$local_database_directory"
fi
mkdir -p -- "$local_database_directory"
"${compose[@]}" up --detach db

ready=false
consecutive_successes=0
for ((attempt = 1; attempt <= 30; attempt++)); do
    if test_local_database_ready; then
        ((consecutive_successes += 1))
        if ((consecutive_successes >= 3)); then
            ready=true
            break
        fi
    else
        consecutive_successes=0
    fi
    sleep 2
done
[[ "$ready" == true ]] || {
    echo 'The local PostgreSQL container did not become ready within 60 seconds.' >&2
    exit 1
}

echo '[3/6] Restoring the snapshot into local PostgreSQL...'
"${compose[@]}" cp "$dump_path" "db:$container_dump_path"
dump_copied_to_container=true
"${compose[@]}" exec -T db pg_restore \
    --username=heartbeat --dbname=heartbeat --single-transaction --exit-on-error \
    --no-owner --no-privileges "$container_dump_path"

echo '[4/6] Checking that the checkout understands the server schema...'
migration_directory="$repository_root/server/Heartbeat.Server/Migrations"
find "$migration_directory" -maxdepth 1 -type f -name '*.cs' \
    ! -name '*.Designer.cs' -exec basename {} .cs \; \
    | awk '/^[0-9]{14}_.+/' | sort -u >"$local_migrations_path"

server_migrations=$("${compose[@]}" exec -T db psql --tuples-only --no-align \
    --username=heartbeat --dbname=heartbeat \
    --command 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";') || {
    echo 'Could not read __EFMigrationsHistory from the restored database.' >&2
    exit 1
}

unknown_migrations=''
while IFS= read -r migration; do
    [[ -n "$migration" ]] || continue
    if ! grep -Fqx -- "$migration" "$local_migrations_path"; then
        if [[ -n "$unknown_migrations" ]]; then
            unknown_migrations+=", $migration"
        else
            unknown_migrations=$migration
        fi
    fi
done <<<"$server_migrations"

if [[ -n "$unknown_migrations" ]]; then
    echo "The server database is newer than this checkout. Update the checkout before starting it. Unknown migrations: $unknown_migrations" >&2
    exit 1
fi

echo '[5/6] Building and starting the local backend and frontend...'
"${compose[@]}" up --detach --build backend frontend

echo '[6/6] Waiting for the local frontend...'
web_ready=false
for ((attempt = 1; attempt <= 60; attempt++)); do
    status_code=$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
        http://127.0.0.1:8080/ || true)
    if [[ "$status_code" =~ ^[234][0-9][0-9]$ ]]; then
        web_ready=true
        break
    fi
    sleep 1
done
[[ "$web_ready" == true ]] || {
    echo 'The stack was started, but http://127.0.0.1:8080 did not become ready within 60 seconds.' >&2
    exit 1
}

echo 'Local data refresh completed: http://localhost:8080'
