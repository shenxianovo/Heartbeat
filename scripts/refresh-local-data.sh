#!/usr/bin/env bash

set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/refresh-local-data.sh [options]

Replace the local E2E PostgreSQL database with a transaction-consistent server snapshot.
The server is read only; only the local Compose PostgreSQL database is replaced.

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
        --username=heartbeat --dbname=postgres --tuples-only --no-align \
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


if [[ "$force" != true ]]; then
    echo "WARNING: This replaces the local heartbeat database with a snapshot containing private server data." >&2
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
progress_pid=''
download_started_at=0
refresh_suffix="$(date +%Y%m%d%H%M%S)_$$"
staging_database="heartbeat_refresh_$refresh_suffix"
backup_database="heartbeat_before_refresh_$refresh_suffix"
replacement_started=false
promotion_attempted=false
refresh_completed=false
previous_services=()

print_download_progress() {
    local elapsed=$((SECONDS - download_started_at))
    local bytes
    bytes=$(wc -c <"$dump_path")
    ((elapsed > 0)) || elapsed=1
    awk -v bytes="$bytes" -v elapsed="$elapsed" \
        'BEGIN { printf "      Received %.1f MiB | average %.1f MiB/s | %ds elapsed", bytes / 1048576, bytes / 1048576 / elapsed, elapsed }' >&2
}

stop_download_progress() {
    if [[ -n "$progress_pid" ]]; then
        kill "$progress_pid" 2>/dev/null || true
        wait "$progress_pid" 2>/dev/null || true
        progress_pid=''
        [[ ! -t 2 ]] || printf '\r' >&2
        print_download_progress
        printf '\n' >&2
    fi
}

local_sql() {
    "${compose[@]}" exec -T db psql --username=heartbeat --dbname=postgres \
        --no-psqlrc --set=ON_ERROR_STOP=1 --tuples-only --no-align --command "$1"
}

rollback_database() {
    echo 'Refresh failed; restoring the previous local database...' >&2
    if [[ "$promotion_attempted" == true ]]; then
        "${compose[@]}" stop backend frontend headless || return 1
        local backup_exists
        backup_exists=$(local_sql "SELECT count(*) FROM pg_database WHERE datname = '$backup_database'") || return 1
        if [[ "$backup_exists" == 1 ]]; then
            local_sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('heartbeat', '$backup_database') AND pid <> pg_backend_pid()" >/dev/null || return 1
            local_sql "BEGIN; ALTER DATABASE heartbeat RENAME TO $staging_database; ALTER DATABASE $backup_database RENAME TO heartbeat; COMMIT;" || return 1
        fi
    fi
    local_sql "DROP DATABASE IF EXISTS $staging_database WITH (FORCE)" || return 1
    if ((${#previous_services[@]} > 0)); then
        "${compose[@]}" up --detach "${previous_services[@]}" || return 1
    fi
    echo 'Previous local database restored.' >&2
}

cleanup() {
    local exit_code=$?
    trap - EXIT
    trap '' INT TERM
    stop_download_progress
    if [[ "$dump_copied_to_container" == true ]]; then
        "${compose[@]}" exec -T db rm -f "$container_dump_path" >/dev/null 2>&1 || true
    fi
    if [[ "$replacement_started" == true && "$refresh_completed" != true ]]; then
        rollback_database || echo "Automatic rollback did not complete. Keep the stack stopped and inspect databases heartbeat, $backup_database and $staging_database." >&2
        ((exit_code != 0)) || exit_code=1
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
trap 'exit 130' INT
trap 'exit 143' TERM

quoted_directory=$(quote_posix_shell_argument "$remote_directory")
quoted_compose_file=$(quote_posix_shell_argument "$remote_compose_file")
quoted_env_file=$(quote_posix_shell_argument "$remote_env_file")
remote_command="set -eu; cd -- $quoted_directory; docker compose --file $quoted_compose_file --env-file $quoted_env_file exec -T db pg_dump --username=heartbeat --dbname=heartbeat --format=custom --compress=6 --no-owner --no-privileges"

ssh_arguments=(-o BatchMode=no -o NumberOfPasswordPrompts=3 -p "$ssh_port")
if [[ -n "$identity_file" ]]; then
    ssh_arguments+=(-i "$identity_file")
fi

echo '[1/6] Streaming a transaction-consistent server snapshot over SSH...'
echo '      Live compressed stream: total size is unknown until pg_dump finishes.' >&2
download_started_at=$SECONDS
(
    trap - EXIT INT TERM
    while sleep 1; do
        # Leave SSH password/host-key prompts alone until bytes start arriving.
        [[ -s "$dump_path" ]] || continue
        # Keep interactive terminals live; avoid one log line per second in redirected output.
        if [[ -t 2 ]]; then
            printf '\r' >&2
            print_download_progress
        elif (((SECONDS - download_started_at) % 5 == 0)); then
            print_download_progress
            printf '\n' >&2
        fi
    done
) &
progress_pid=$!
# SSH stays in the foreground so password and host-key prompts can use the terminal.
ssh "${ssh_arguments[@]}" "$ssh_destination" "$remote_command" >"$dump_path"
stop_download_progress

if [[ $(LC_ALL=C dd if="$dump_path" bs=5 count=1 2>/dev/null) != 'PGDMP' ]]; then
    echo 'The downloaded file is not a PostgreSQL custom-format dump.' >&2
    exit 1
fi
size_mib=$(du -m "$dump_path" | awk '{print $1}')
echo "      Downloaded ${size_mib} MiB."

echo '[2/6] Stopping writers and preparing an isolated restore database...'
running_services=$("${compose[@]}" ps --services --status running)
services_to_stop=()
while IFS= read -r service; do
    if [[ -n "$service" ]]; then
        previous_services+=("$service")
        [[ "$service" == db ]] || services_to_stop+=("$service")
    fi
done <<<"$running_services"
replacement_started=true
if ((${#services_to_stop[@]} > 0)); then
    "${compose[@]}" stop "${services_to_stop[@]}"
fi
# Keep the PostgreSQL data directory and its bind mount stable throughout the refresh.
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

echo '[3/6] Restoring the snapshot into an empty staging database...'
"${compose[@]}" exec -T db createdb --username=heartbeat --template=template0 "$staging_database"
"${compose[@]}" cp "$dump_path" "db:$container_dump_path"
dump_copied_to_container=true
"${compose[@]}" exec -T db pg_restore \
    --username=heartbeat --dbname="$staging_database" --single-transaction --exit-on-error \
    --no-owner --no-privileges "$container_dump_path"

echo '[4/6] Checking that the checkout understands the server schema...'
migration_directory="$repository_root/server/Heartbeat.Server/Migrations"
find "$migration_directory" -maxdepth 1 -type f -name '*.cs' \
    ! -name '*.Designer.cs' -exec basename {} .cs \; \
    | awk '/^[0-9]{14}_.+/' | sort -u >"$local_migrations_path"

server_migrations=$("${compose[@]}" exec -T db psql --tuples-only --no-align \
    --username=heartbeat --dbname="$staging_database" --no-psqlrc --set=ON_ERROR_STOP=1 \
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

echo '[5/6] Promoting the restored database and starting the local backend and frontend...'
# Both renames commit together. Keep the original database until the new stack is healthy.
promotion_attempted=true
local_sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname IN ('heartbeat', '$staging_database') AND pid <> pg_backend_pid()" >/dev/null
local_sql "BEGIN; ALTER DATABASE heartbeat RENAME TO $backup_database; ALTER DATABASE $staging_database RENAME TO heartbeat; COMMIT;"
"${compose[@]}" up --detach --build backend frontend

echo '[6/6] Waiting for Analytics through the local frontend...'
web_ready=false
for ((attempt = 1; attempt <= 60; attempt++)); do
    status_code=$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' \
        http://127.0.0.1:8080/health || true)
    if [[ "$status_code" == 200 ]]; then
        web_ready=true
        break
    fi
    sleep 1
done
[[ "$web_ready" == true ]] || {
    echo 'Analytics did not become ready through http://127.0.0.1:8080/health within 60 seconds.' >&2
    exit 1
}

# Resume other previously running services only after Analytics is ready.
if ((${#services_to_stop[@]} > 0)); then
    "${compose[@]}" up --detach "${services_to_stop[@]}"
fi
refresh_completed=true
if ! local_sql "DROP DATABASE $backup_database WITH (FORCE)"; then
    echo "Refresh succeeded, but the sensitive previous database could not be removed: $backup_database" >&2
fi
echo 'Local data refresh completed: http://localhost:8080'
