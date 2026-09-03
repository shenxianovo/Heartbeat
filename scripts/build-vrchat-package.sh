#!/usr/bin/env bash

set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/build-vrchat-package.sh [options]

Build the VRChat Collector Package into a host directory. The Package is built inside a
linux container, so its artifact selector matches the headless Hub container rather than
the host running this script.

Options:
  --output PATH  Output directory (default: .local/collector-packages/vrchat)
  -h, --help     Show this help
EOF
}

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repository_root=$(cd -- "$script_directory/.." && pwd -P)
output_directory="$repository_root/.local/collector-packages/vrchat"

while (($# > 0)); do
    case "$1" in
        --output)
            [[ $# -ge 2 ]] || { echo 'Missing value for --output.' >&2; exit 2; }
            output_directory=$2
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

[[ -n "$output_directory" ]] || { echo 'Output directory must not be empty.' >&2; exit 2; }
[[ "$output_directory" = /* ]] || output_directory="$repository_root/$output_directory"

if [[ -L "$output_directory" ]]; then
    echo "Output path must not be a symbolic link: $output_directory" >&2
    exit 1
fi
output_parent=$(dirname -- "$output_directory")
output_name=$(basename -- "$output_directory")
if [[ "$output_name" == . || "$output_name" == .. ]]; then
    echo "Refusing unsafe output path: $output_directory" >&2
    exit 1
fi
mkdir -p -- "$output_parent"
output_parent=$(cd -- "$output_parent" && pwd -P)
if [[ "$output_name" == / ]]; then
    output_directory=/
else
    output_directory="$output_parent/$output_name"
fi

if [[ "$output_directory" == / ||
      "$repository_root" == "$output_directory" ||
      "$repository_root" == "$output_directory/"* ]]; then
    echo "Refusing unsafe output path: $output_directory" >&2
    exit 1
fi

dockerfile="$repository_root/collection/collectors/Heartbeat.Collector.VRChat/Dockerfile"
[[ -f "$dockerfile" ]] || { echo "Dockerfile not found: $dockerfile" >&2; exit 1; }
command -v docker >/dev/null 2>&1 || { echo 'docker is required.' >&2; exit 1; }
docker info >/dev/null 2>&1 || {
    echo 'Docker is not ready. Start Docker Desktop and wait for the engine to finish starting.' >&2
    exit 1
}

if [[ -e "$output_directory" && ! -d "$output_directory" ]]; then
    echo "Output path exists and is not a directory: $output_directory" >&2
    exit 1
fi

ownership_marker='.heartbeat-vrchat-package-output'
ownership_value='heartbeat-vrchat-package-output-v1'
if [[ -d "$output_directory" ]] &&
   [[ -n "$(find "$output_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]] &&
   ! grep -Fqx -- "$ownership_value" "$output_directory/$ownership_marker" 2>/dev/null; then
    echo "Output directory is non-empty and not owned by this tool: $output_directory" >&2
    exit 1
fi

staging_directory=$(mktemp -d "$output_parent/.${output_name}.build.XXXXXX")
cleanup() {
    if [[ -n "${staging_directory:-}" && -d "$staging_directory" ]]; then
        rm -rf -- "$staging_directory"
    fi
}
trap cleanup EXIT

echo "[1/2] Building the VRChat Collector Package in $staging_directory..."
(
    cd -- "$repository_root"
    docker build \
        --file collection/collectors/Heartbeat.Collector.VRChat/Dockerfile \
        --target package \
        --output "type=local,dest=$staging_directory" \
        .
)

[[ -f "$staging_directory/collector-manifest.json" ]] || {
    echo "Build finished but collector-manifest.json is missing from the staged output." >&2
    exit 1
}
printf '%s\n' "$ownership_value" >"$staging_directory/$ownership_marker"

echo "[2/2] Replacing the tool-owned output at $output_directory..."
if [[ -d "$output_directory" ]]; then
    if [[ -n "$(find "$output_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
        rm -rf -- "$output_directory"
    else
        rmdir -- "$output_directory"
    fi
fi
mv -- "$staging_directory" "$output_directory"
staging_directory=''

echo "VRChat Collector Package ready: $output_directory"
echo "Use package-vrchat-release.sh or the dedicated tag workflow to publish it to the Registry."
