#!/usr/bin/env bash
set -Eeuo pipefail
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
command -v node >/dev/null 2>&1 || { echo 'Node.js 24+ is required for local development.' >&2; exit 1; }
exec node "$script_directory/start-local.mjs" "$@"
