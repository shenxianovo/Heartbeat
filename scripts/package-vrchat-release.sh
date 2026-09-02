#!/usr/bin/env bash

set -Eeuo pipefail

package_id='heartbeat.collector.vrchat'
default_base_url='https://heartbeat.shenxianovo.com/collector-registry/v1'

usage() {
    cat <<'EOF'
Usage: ./scripts/package-vrchat-release.sh --package PATH --version X.Y.Z --output PATH [options]

Create the immutable Web release files for one already-built linux-x64 VRChat Collector Package.
The output contains the Package zip and release.json. It does not upload or install anything.

Required:
  --package PATH   Collector Package directory containing collector-manifest.json
  --version X.Y.Z  Exact stable SemVer; must match the Package manifest
  --output PATH    New or empty output directory

Options:
  --base-url URL   Registry root (default: https://heartbeat.shenxianovo.com/collector-registry/v1)
  -h, --help       Show this help
EOF
}

package_directory=''
version=''
output_directory=''
base_url=$default_base_url

while (($# > 0)); do
    case "$1" in
        --package)
            [[ $# -ge 2 ]] || { echo 'Missing value for --package.' >&2; exit 2; }
            package_directory=$2
            shift 2
            ;;
        --version)
            [[ $# -ge 2 ]] || { echo 'Missing value for --version.' >&2; exit 2; }
            version=$2
            shift 2
            ;;
        --output)
            [[ $# -ge 2 ]] || { echo 'Missing value for --output.' >&2; exit 2; }
            output_directory=$2
            shift 2
            ;;
        --base-url)
            [[ $# -ge 2 ]] || { echo 'Missing value for --base-url.' >&2; exit 2; }
            base_url=$2
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

[[ -n "$package_directory" ]] || { echo '--package is required.' >&2; exit 2; }
[[ -n "$version" ]] || { echo '--version is required.' >&2; exit 2; }
[[ -n "$output_directory" ]] || { echo '--output is required.' >&2; exit 2; }
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || {
    echo "Version must be a stable X.Y.Z SemVer: $version" >&2
    exit 2
}
[[ "$base_url" =~ ^https://[^/]+(/.*)?$ ]] || {
    echo "Registry base URL must be HTTPS: $base_url" >&2
    exit 2
}
base_url=${base_url%/}

command -v jq >/dev/null 2>&1 || { echo 'jq is required.' >&2; exit 1; }
command -v zip >/dev/null 2>&1 || { echo 'zip is required.' >&2; exit 1; }

package_directory=$(cd -- "$package_directory" 2>/dev/null && pwd -P) || {
    echo "Package directory does not exist: $package_directory" >&2
    exit 1
}
manifest_path="$package_directory/collector-manifest.json"
[[ -f "$manifest_path" ]] || { echo "collector-manifest.json is missing: $manifest_path" >&2; exit 1; }

output_parent=$(dirname -- "$output_directory")
output_name=$(basename -- "$output_directory")
[[ "$output_name" != / && "$output_name" != . && "$output_name" != .. ]] || {
    echo "Refusing unsafe output path: $output_directory" >&2
    exit 1
}
mkdir -p -- "$output_parent"
output_parent=$(cd -- "$output_parent" && pwd -P)
output_directory="$output_parent/$output_name"
case "$output_directory/" in
    "$package_directory/"*)
        echo "Output directory must not overlap the Package directory: $output_directory" >&2
        exit 1
        ;;
esac
case "$package_directory/" in
    "$output_directory/"*)
        echo "Output directory must not overlap the Package directory: $output_directory" >&2
        exit 1
        ;;
esac
if [[ -e "$output_directory" && ! -d "$output_directory" ]]; then
    echo "Output path exists and is not a directory: $output_directory" >&2
    exit 1
fi
if [[ -L "$output_directory" ]]; then
    echo "Output path must not be a symbolic link: $output_directory" >&2
    exit 1
fi
if [[ -d "$output_directory" && -n "$(find "$output_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
    echo "Output directory must be empty: $output_directory" >&2
    exit 1
fi

if find "$package_directory" -type l -print -quit | grep -q .; then
    echo 'Collector Package must not contain symbolic links.' >&2
    exit 1
fi
if ! jq -e \
    --arg packageId "$package_id" \
    --arg version "$version" \
    '
      .manifestVersion == 1 and
      .packageId == $packageId and
      .version == $version and
      ([.artifacts[] |
        select(
          .selector.driver == "managedProcess" and
          (.selector.os | index("linux")) != null and
          (.selector.arch | index("x64")) != null
        )] | length) == 1
    ' "$manifest_path" >/dev/null; then
    echo "Package manifest must identify $package_id $version with one linux-x64 ManagedProcess artifact." >&2
    exit 1
fi

mkdir -p -- "$output_directory"
staging_directory=$(mktemp -d "${TMPDIR:-/tmp}/heartbeat-vrchat-release.XXXXXX")
cleanup() {
    if [[ -d "${staging_directory:-}" ]]; then
        rm -rf -- "$staging_directory"
    fi
}
trap cleanup EXIT

mkdir -p -- "$staging_directory/package"
cp -R "$package_directory/". "$staging_directory/package/"
# Local build scripts keep this sibling-replacement ownership marker in their output directory;
# it belongs to the tool, not to the published Collector Package.
rm -f -- "$staging_directory/package/.heartbeat-vrchat-package-output"
find "$staging_directory/package" -exec touch -t 198001010000 {} +

artifact_name="$package_id-$version-linux-x64.zip"
artifact_path="$output_directory/$artifact_name"
(
    cd -- "$staging_directory/package"
    find . -type f -print | LC_ALL=C sort | zip -X -q "$artifact_path" -@
)

if command -v sha256sum >/dev/null 2>&1; then
    artifact_sha256=$(sha256sum "$artifact_path" | awk '{print $1}')
else
    artifact_sha256=$(shasum -a 256 "$artifact_path" | awk '{print $1}')
fi
artifact_length=$(wc -c < "$artifact_path" | tr -d '[:space:]')
artifact_url="$base_url/packages/$package_id/versions/$version/$artifact_name"

jq -n \
    --arg packageId "$package_id" \
    --arg version "$version" \
    --arg fileName "$artifact_name" \
    --arg url "$artifact_url" \
    --arg sha256 "sha256:$artifact_sha256" \
    --argjson length "$artifact_length" \
    '{
      schemaVersion: 1,
      packageId: $packageId,
      version: $version,
      target: { os: "linux", arch: "x64" },
      artifact: {
        fileName: $fileName,
        url: $url,
        length: $length,
        sha256: $sha256
      }
    }' > "$output_directory/release.json"

echo "VRChat Collector release ready: $output_directory"
