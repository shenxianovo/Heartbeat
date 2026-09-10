#!/usr/bin/env bash
# Run from /srv/heartbeat. CI uploads this script + compose to a unique release directory.
set -Eeuo pipefail
umask 077
phase=${1:?Expected migrate or deploy}
export BACKEND_IMAGE=${2:?Expected immutable Analytics image digest}
[[ "$phase" == migrate || "$phase" == deploy ]] || exit 2
[[ "$BACKEND_IMAGE" =~ ^ghcr\.io/shenxianovo/heartbeat-backend@sha256:[a-f0-9]{64}$ ]] || {
    echo 'Analytics deployment requires an immutable image digest.' >&2
    exit 2
}

# GitHub serializes the entire workflow. This also guards a remote process surviving SSH loss.
exec 9>.analytics-deploy.lock
flock -n 9 || { echo 'Another Analytics deployment is still running.' >&2; exit 1; }
if docker inspect heartbeat-analytics-migration >/dev/null 2>&1; then
    echo 'A migration container remains; inspect it before retrying the release.' >&2
    exit 1
fi
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
mkdir -p .analytics-release backups

if [[ "$phase" == migrate ]]; then
    cp "$script_directory/../compose.yml" compose.yml
    docker compose pull backend
    docker compose up -d --wait db
    # Invalidate any previous success before stopping writes or touching the database.
    rm -f .analytics-release/ready-image
    backup="backups/analytics-$(date -u +%Y%m%dT%H%M%SZ)-$$"
    printf '%s\n' "$backup" > .analytics-release/backup
    previous=$(docker compose ps --all --quiet backend)
    if [[ -n "$previous" ]]; then
        previous_image=$(docker inspect --format '{{.Image}}' "$previous")
        docker image inspect --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}' \
            "$previous_image" > "$backup.previous-image"
    fi
    docker compose stop backend
    echo "Analytics writes stopped. Creating pre-upgrade backup: $backup.dump"
    docker compose exec -T db pg_dump -U heartbeat -d heartbeat -Fc -Z1 > "$backup.dump"
    docker compose exec -T db pg_restore --list < "$backup.dump" > /dev/null
    echo 'Applying database migrations and C# backfills; SQL command timeout is unlimited.'
    docker compose run --rm --no-deps -T --name heartbeat-analytics-migration \
        -e DatabaseMigration__CommandTimeoutSeconds=0 backend --migrate 2>&1 | tee "$backup.migration.log"
    printf '%s\n' "$BACKEND_IMAGE" > .analytics-release/ready-image
    echo 'Migration completed. Analytics stays stopped until the deploy job succeeds.'
    exit 0
fi

[[ -f .analytics-release/ready-image && "$(cat .analytics-release/ready-image)" == "$BACKEND_IMAGE" ]] || {
    echo 'No successful migration for this image. Run the migration job first.' >&2
    exit 1
}
docker compose run --rm --no-deps -T backend --check-database
docker compose up -d --no-deps --force-recreate backend

# Probe the new Analytics container directly, avoiding an old/proxied public health response.
# The larger startup window also covers catalog reconciliation on a 1C1G server.
deadline=$((SECONDS + 1800))
while (( SECONDS < deadline )); do
    container=$(docker compose ps --all --quiet backend)
    read -r state restarts oom <<< "$(docker inspect --format '{{.State.Status}} {{.RestartCount}} {{.State.OOMKilled}}' "$container")"
    if [[ "$state" != running || "$restarts" != 0 || "$oom" != false ]]; then
        echo "Analytics failed during startup: state=$state, restarts=$restarts, OOM=$oom" >&2
        break
    fi
    if docker compose exec -T db wget -q -O /dev/null -T 5 http://backend:8080/health; then
        # Persist the exact image for subsequent ordinary compose operations, preserving other settings.
        awk '!/^BACKEND_IMAGE=/' .env > .analytics-release/env.next
        printf 'BACKEND_IMAGE=%s\n' "$BACKEND_IMAGE" >> .analytics-release/env.next
        mv .analytics-release/env.next .env
        backup=$(cat .analytics-release/backup)
        touch "$backup.success"
        # Keep the two newest successful backups. Failed/unverified releases have no success marker.
        shopt -s nullglob
        successful=(backups/analytics-*.success)
        for ((i=0; i<${#successful[@]}-2; i++)); do
            obsolete=${successful[i]%.success}
            rm -f "$obsolete.dump" "$obsolete.previous-image" "$obsolete.migration.log" "$obsolete.success"
        done
        docker compose ps backend
        echo "Analytics healthy; deployed $BACKEND_IMAGE"
        exit 0
    fi
    echo 'Analytics is still starting; waiting for health...'
    sleep 10
done

docker compose ps --all backend
docker compose logs --no-color --tail=200 backend
docker compose stop backend
echo 'Analytics did not become healthy. Writes remain stopped; keep the backup and inspect the failure.' >&2
exit 1
