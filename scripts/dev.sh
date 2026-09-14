#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_dir}/.." && pwd)"
invocation_dir="$(pwd)"
default_env_file="${repository_root}/.env.local"

usage() {
  cat <<'EOF'
Usage: ./scripts/dev.sh [command] [options] [services...]

Commands:
  up       Build and start the selected services (default command)
  logs     Follow logs for the selected container services
  status   Show status for the selected container services
  down     Stop and remove the selected container services, preserving data
  reset    Stop the whole local stack and delete all local Docker data

Services:
  web api db hub desktop

With no service selection, web, api, and db are selected. An explicit selection
replaces those defaults. Starting web also starts api, migrate, and db; starting
api also starts migrate and db; starting desktop also starts hub. Desktop runs
as a foreground macOS host process and stops with Ctrl+C.

Options:
  --release        For "up", use the production images instead of watch mode
  --env-file PATH  Use PATH instead of the repository .env.local
  -h, --help       Show this help
EOF
}

fail_usage() {
  printf '%s\n' "$1" >&2
  printf 'Run ./scripts/dev.sh --help for usage.\n' >&2
  exit 2
}

resolve_path() {
  case "$1" in
    /*) printf '%s\n' "$1" ;;
    *) printf '%s/%s\n' "${invocation_dir}" "$1" ;;
  esac
}

env_file_value() {
  local key="$1"
  local value=""
  local line=""

  if [[ -n "${!key:-}" ]]; then
    printf '%s\n' "${!key}"
    return
  fi

  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%$'\r'}"
    if [[ "${line}" == "${key}="* ]]; then
      value="${line#*=}"
    fi
  done < "${env_file}"

  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  if [[ ${#value} -ge 2 && "${value:0:1}" == "'" && "${value: -1}" == "'" ]]; then
    local decoded="" index char next
    value="${value:1:${#value}-2}"
    for ((index = 0; index < ${#value}; index++)); do
      char="${value:index:1}"
      if [[ "${char}" == "\\" && $((index + 1)) -lt ${#value} ]]; then
        next="${value:index+1:1}"
        if [[ "${next}" == "'" ]]; then
          decoded+="'"
          index=$((index + 1))
          continue
        fi
      fi
      decoded+="${char}"
    done
    value="${decoded}"
  elif [[ ${#value} -ge 2 && "${value:0:1}" == '"' && "${value: -1}" == '"' ]]; then
    local decoded="" index char next
    value="${value:1:${#value}-2}"
    for ((index = 0; index < ${#value}; index++)); do
      char="${value:index:1}"
      if [[ "${char}" == "\\" && $((index + 1)) -lt ${#value} ]]; then
        index=$((index + 1))
        next="${value:index:1}"
        case "${next}" in
          n) decoded+=$'\n' ;;
          r) decoded+=$'\r' ;;
          t) decoded+=$'\t' ;;
          '"'|'\') decoded+="${next}" ;;
          *) decoded+="\\${next}" ;;
        esac
      else
        decoded+="${char}"
      fi
    done
    value="${decoded}"
  else
    value="${value%%[[:space:]]#*}"
    value="${value%"${value##*[![:space:]]}"}"
  fi
  printf '%s\n' "${value}"
}

require_hub_configuration() {
  local missing=()
  local key
  for key in HEARTBEAT_API_KEY HEARTBEAT_OWNER_ID HEARTBEAT_HUB_TOKEN; do
    if [[ -z "$(env_file_value "${key}")" ]]; then
      missing+=("${key}")
    fi
  done
  if (( ${#missing[@]} > 0 )); then
    printf 'Hub configuration is incomplete (%s). Run ./scripts/setup.sh first.\n' \
      "$(IFS=,; printf '%s' "${missing[*]}")" >&2
    exit 1
  fi
}

check_service_has_not_exited() {
  local service="$1" container state
  container="$("${compose[@]}" ps --all --quiet "${service}")"
  [[ -n "${container}" ]] || return 0
  state="$(docker inspect --format '{{.State.Status}}' "${container}" 2>/dev/null || true)"
  case "${state}" in
    exited|dead)
      printf '%s exited during startup. Recent logs:\n' "${service}" >&2
      "${compose[@]}" logs --tail 40 "${service}" >&2 || true
      return 1
      ;;
  esac
}

wait_for_http() {
  local service="$1" url="$2" expected_status="$3" status=""
  local deadline=$((SECONDS + startup_timeout_seconds))
  printf 'Waiting for %s at %s ...\n' "${service}" "${url}"
  while (( SECONDS < deadline )); do
    check_service_has_not_exited "${service}" || exit 1
    status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
      --max-time 2 "${url}" 2>/dev/null || true)"
    if [[ "${status}" == "${expected_status}" ]]; then
      printf 'Ready: %s\n' "${url}"
      return
    fi
    sleep 1
  done
  printf '%s did not become ready within %s seconds. Check ./scripts/dev.sh logs %s.\n' \
    "${service}" "${startup_timeout_seconds}" "${service}" >&2
  exit 1
}

wait_for_db() {
  local deadline=$((SECONDS + startup_timeout_seconds))
  printf 'Waiting for db at 127.0.0.1:54329 ...\n'
  while (( SECONDS < deadline )); do
    check_service_has_not_exited db || exit 1
    if "${compose[@]}" exec --no-TTY db pg_isready -U heartbeat -d heartbeat >/dev/null 2>&1; then
      printf 'Ready: 127.0.0.1:54329\n'
      return
    fi
    sleep 1
  done
  printf 'db did not become ready within %s seconds. Check ./scripts/dev.sh logs db.\n' \
    "${startup_timeout_seconds}" >&2
  exit 1
}

command="up"
if (( $# > 0 )) && [[ "$1" != -* ]]; then
  case "$1" in
    up|logs|status|down|reset)
      command="$1"
      shift
      ;;
    web|api|db|hub|desktop)
      ;;
    *)
      fail_usage "Unknown command or service: $1"
      ;;
  esac
fi

release=false
env_file="${default_env_file}"
custom_env_file=false
selected_web=false
selected_api=false
selected_db=false
selected_hub=false
selected_desktop=false
explicit_selection=false

while (( $# > 0 )); do
  case "$1" in
    --release)
      release=true
      ;;
    --env-file)
      shift
      (( $# > 0 )) || fail_usage "Missing path for --env-file."
      env_file="$(resolve_path "$1")"
      custom_env_file=true
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    web)
      selected_web=true
      explicit_selection=true
      ;;
    api)
      selected_api=true
      explicit_selection=true
      ;;
    db)
      selected_db=true
      explicit_selection=true
      ;;
    hub)
      selected_hub=true
      explicit_selection=true
      ;;
    desktop)
      selected_desktop=true
      explicit_selection=true
      ;;
    *)
      fail_usage "Unknown option or service: $1"
      ;;
  esac
  shift
done

if [[ "${release}" == true && "${command}" != "up" ]]; then
  fail_usage "--release is only valid with up."
fi

if [[ "${command}" == "reset" ]]; then
  if [[ "${explicit_selection}" == true ]]; then
    fail_usage "reset does not accept services; it always deletes data for the whole local stack."
  fi
else
  if [[ "${explicit_selection}" == false ]]; then
    selected_web=true
    selected_api=true
    selected_db=true
  fi
fi

if [[ "${command}" != "up" && "${selected_desktop}" == true ]]; then
  fail_usage "desktop is a foreground host process; stop it with Ctrl+C where it is running."
fi

if [[ "${custom_env_file}" == true && ! -f "${env_file}" ]]; then
  printf 'Environment file not found: %s\n' "${env_file}" >&2
  exit 1
fi
if [[ ! -f "${env_file}" ]]; then
  umask 077
  : > "${env_file}"
fi

cd "${repository_root}"

compose=(docker compose --project-directory "${repository_root}" --env-file "${env_file}" --file "${repository_root}/compose.yaml")
if [[ "${release}" == false ]]; then
  compose+=(--file "${repository_root}/compose.dev.yaml")
fi

case "${command}" in
  up)
    if [[ "${selected_web}" == true ]]; then
      selected_api=true
    fi
    if [[ "${selected_api}" == true ]]; then
      selected_db=true
    fi
    if [[ "${selected_desktop}" == true ]]; then
      selected_hub=true
    fi

    collector_target=""
    collector_display_name=""
    if [[ "${selected_desktop}" == true ]]; then
      if [[ "$(uname -s)" != "Darwin" ]]; then
        printf 'desktop requires macOS.\n' >&2
        exit 1
      fi
      collector_target="$(env_file_value HEARTBEAT_COLLECTOR_TARGET)"
      collector_display_name="$(env_file_value HEARTBEAT_COLLECTOR_DISPLAY_NAME)"
      if [[ -z "${collector_target}" ]]; then
        printf 'Desktop Collector configuration is incomplete (HEARTBEAT_COLLECTOR_TARGET). Run ./scripts/setup.sh first.\n' >&2
        exit 1
      fi
      command -v dotnet >/dev/null 2>&1 || {
        printf 'The .NET 10 SDK is required to run desktop.\n' >&2
        exit 1
      }
    fi
    if [[ "${selected_hub}" == true ]]; then
      require_hub_configuration
    fi

    startup_timeout_seconds="${HEARTBEAT_START_TIMEOUT_SECONDS:-180}"
    if [[ ! "${startup_timeout_seconds}" =~ ^[1-9][0-9]*$ ]]; then
      printf 'HEARTBEAT_START_TIMEOUT_SECONDS must be a positive integer.\n' >&2
      exit 2
    fi
    if [[ "${selected_api}" == true || "${selected_web}" == true || "${selected_hub}" == true ]]; then
      command -v curl >/dev/null 2>&1 || {
        printf 'curl is required for local service readiness checks.\n' >&2
        exit 1
      }
    fi

    "${compose[@]}" config --quiet

    services=()
    [[ "${selected_db}" == true ]] && services+=(db)
    [[ "${selected_api}" == true ]] && services+=(migrate api)
    [[ "${selected_web}" == true ]] && services+=(web)
    [[ "${selected_hub}" == true ]] && services+=(hub)
    "${compose[@]}" up --build --detach "${services[@]}"

    [[ "${selected_db}" == true ]] && wait_for_db
    [[ "${selected_api}" == true ]] && wait_for_http api http://127.0.0.1:8080/health/ready 200
    [[ "${selected_web}" == true ]] && wait_for_http web http://127.0.0.1:3000/ 200
    [[ "${selected_hub}" == true ]] && wait_for_http hub http://127.0.0.1:4318/hub/v1/status 401

    if [[ "${selected_desktop}" == true ]]; then
      export HEARTBEAT_HUB_URL="http://127.0.0.1:4318"
      export HEARTBEAT_HUB_TOKEN="$(env_file_value HEARTBEAT_HUB_TOKEN)"
      export HEARTBEAT_COLLECTOR_TARGET="${collector_target}"
      export HEARTBEAT_COLLECTOR_DISPLAY_NAME="${collector_display_name}"
      collector_project="${repository_root}/src/Collectors/Heartbeat.Collector.Desktop.Mac/Heartbeat.Collector.Desktop.Mac.csproj"
      printf 'Hub is running in Docker. Desktop Collector is attached to this terminal; press Ctrl+C to stop it.\n'
      if [[ "${release}" == true ]]; then
        exec dotnet run --configuration Release --project "${collector_project}"
      fi
      exec dotnet watch --project "${collector_project}" run --no-launch-profile
    fi
    ;;
  logs)
    services=()
    [[ "${selected_web}" == true ]] && services+=(web)
    [[ "${selected_api}" == true ]] && services+=(api)
    [[ "${selected_db}" == true ]] && services+=(db)
    [[ "${selected_hub}" == true ]] && services+=(hub)
    "${compose[@]}" logs --follow "${services[@]}"
    ;;
  status)
    services=()
    [[ "${selected_web}" == true ]] && services+=(web)
    [[ "${selected_api}" == true ]] && services+=(api)
    [[ "${selected_db}" == true ]] && services+=(db)
    [[ "${selected_hub}" == true ]] && services+=(hub)
    "${compose[@]}" ps --all "${services[@]}"
    ;;
  down)
    services=()
    [[ "${selected_web}" == true ]] && services+=(web)
    [[ "${selected_api}" == true ]] && services+=(api migrate)
    [[ "${selected_db}" == true ]] && services+=(db)
    [[ "${selected_hub}" == true ]] && services+=(hub)
    "${compose[@]}" rm --stop --force "${services[@]}"
    ;;
  reset)
    printf 'Deleting all Heartbeat local Docker containers, networks, and volumes.\n'
    "${compose[@]}" down --volumes --remove-orphans
    ;;
esac
