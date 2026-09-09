#!/usr/bin/env bash
set -Eeuo pipefail

compose=(docker compose)
health_url=http://127.0.0.1:8080/health
hub_url=
timeout_seconds=1800
while (($# > 0)); do
    [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
    case "$1" in
        --compose-file) compose+=(--file "$2") ;;
        --env-file) compose+=(--env-file "$2") ;;
        --health-url) health_url=$2 ;;
        --hub-url) hub_url=$2 ;;
        --timeout-seconds) timeout_seconds=$2 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
    shift 2
done
[[ "$timeout_seconds" =~ ^[1-9][0-9]*$ ]] || { echo 'Timeout must be a positive number of seconds.' >&2; exit 2; }

services=(backend)
[[ -z "$hub_url" ]] || services+=(headless)
initial_restarts=()
analytics_status=000
hub_status=not-requested
started=$SECONDS
next_progress=0

diagnostics() {
    "${compose[@]}" ps --all "${services[@]}" >&2 || true
    echo 'Inspect service logs with docker compose logs (using the same compose/env files).' >&2
}

while ((SECONDS - started < timeout_seconds)); do
    for index in "${!services[@]}"; do
        service=${services[$index]}
        container=$("${compose[@]}" ps --all --quiet "$service")
        if [[ -z "$container" ]]; then
            echo "$service failed: container is missing." >&2
            diagnostics
            exit 1
        fi
        read -r state exit_code oom restarts <<< "$(docker inspect --format \
            '{{.State.Status}} {{.State.ExitCode}} {{.State.OOMKilled}} {{.RestartCount}}' "$container")"
        initial_restarts[$index]=${initial_restarts[$index]:-$restarts}
        if [[ "$state" != running || "$oom" == true || "$restarts" != "${initial_restarts[$index]}" ]]; then
            echo "$service failed: state=$state, exit=$exit_code, OOM=$oom, restarts=$restarts. Waiting longer will not fix a failed startup." >&2
            diagnostics
            exit 1
        fi
    done

    analytics_status=$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' "$health_url" || true)
    hub_ready=true
    if [[ -n "$hub_url" ]]; then
        hub_status=$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' "$hub_url" || true)
        [[ "$hub_status" == 401 || "$hub_status" == 403 ]] || hub_ready=false
    fi
    if [[ "$analytics_status" == 200 && "$hub_ready" == true ]]; then
        echo "Stack ready after $((SECONDS - started)) seconds."
        exit 0
    fi
    if ((SECONDS - started >= next_progress)); then
        echo "Waiting for startup/migrations: $((SECONDS - started))/${timeout_seconds}s (Analytics: $analytics_status, Headless Hub: $hub_status)."
        next_progress=$((SECONDS - started + 15))
    fi
    sleep 1
done
echo "Stack did not become ready within ${timeout_seconds}s (Analytics: $analytics_status, Headless Hub: $hub_status). The database may still be migrating; inspect its logs before restarting." >&2
diagnostics
exit 1
