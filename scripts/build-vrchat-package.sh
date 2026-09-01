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

# 残留文件会进入 Package 的 tree hash 并可能让安装校验失败，所以每次都从空目录开始。
echo "[1/2] Clearing $output_directory..."
rm -rf -- "$output_directory"
mkdir -p -- "$output_directory"

echo '[2/2] Building the VRChat Collector Package...'
(
    cd -- "$repository_root"
    docker build \
        --file collection/collectors/Heartbeat.Collector.VRChat/Dockerfile \
        --target package \
        --output "type=local,dest=$output_directory" \
        .
)

[[ -f "$output_directory/collector-manifest.json" ]] || {
    echo "Build finished but $output_directory/collector-manifest.json is missing." >&2
    exit 1
}

echo "VRChat Collector Package ready: $output_directory"
echo "Point the headless Hub at its parent directory as a read-only Package source"
echo "(HEADLESS_PACKAGE_SOURCE_PATH), and keep packageDirectory at /package-source/vrchat."
