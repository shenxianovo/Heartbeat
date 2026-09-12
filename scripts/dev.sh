#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_dir}/.." && pwd)"

cd "${repository_root}"

command="${1:-up}"

case "${command}" in
  up)
    docker compose up --build --detach
    ;;
  down)
    docker compose down
    ;;
  logs)
    docker compose logs --follow
    ;;
  status)
    docker compose ps --all
    ;;
  *)
    echo "Usage: $0 [up|down|logs|status]" >&2
    exit 2
    ;;
esac
