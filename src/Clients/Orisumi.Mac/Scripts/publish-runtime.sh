#!/bin/sh
# Xcode build phase: publishes Runtime into Contents/Helpers/Runtime and the foreground state Observer as a bundled
# package into Contents/Resources/Observers, and signs both.
set -eu

export PATH="/usr/local/share/dotnet:$HOME/.dotnet:/opt/homebrew/bin:$PATH"
# The physical path: MSBuild loses project references when one project is reached through a symlink, such as /tmp.
repository="$(cd "$SRCROOT/../../.." && pwd -P)"
contents="$TARGET_BUILD_DIR/$CONTENTS_FOLDER_PATH"
sign="${EXPANDED_CODE_SIGN_IDENTITY:--}"

case "$(uname -m)" in
  arm64) target=osx-arm64 ;;
  *) target=osx-x64 ;;
esac

# MSBuild reads environment variables as properties (Xcode exports TARGETNAME, CONFIGURATION, ...), so run it with a
# clean environment. Build output stays in DerivedData, so a running AppHost watch here does not restart.
dotnet_clean() {
  env -i HOME="$HOME" PATH="$PATH" TMPDIR="${TMPDIR:-/tmp}" USER="${USER:-}" dotnet "$@"
}

runtime="$contents/Helpers/Runtime"
rm -rf "$runtime"
dotnet_clean publish "$repository/src/Observers/Orisumi.Observer.Runtime" \
  --configuration Release --runtime "$target" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  --artifacts-path "$DERIVED_FILE_DIR/dotnet" --output "$runtime"
codesign --force --sign "$sign" "$runtime/Orisumi.Observer.Runtime"

# The package: observer.json and bin/ at the ZIP's root, see Clients · Packages.
project="$repository/src/Observers/Orisumi.Observer.ForegroundState"
version="$(dotnet_clean msbuild "$project" -getProperty:Version | tr -d '[:space:]')"
staging="$DERIVED_FILE_DIR/package/foreground-state"
rm -rf "$staging"
mkdir -p "$staging/bin"
dotnet_clean publish "$project" \
  --configuration Release --runtime "$target" --self-contained -p:DebugType=none \
  --artifacts-path "$DERIVED_FILE_DIR/dotnet" --output "$staging/bin"
find "$staging/bin" -type f \( -perm -u+x -o -name '*.dylib' \) -exec codesign --force --sign "$sign" {} \;
cat > "$staging/observer.json" <<JSON
{
  "format": 1,
  "id": "foreground-state",
  "name": "Foreground State",
  "version": "$version",
  "target": "$target",
  "protocol": 1,
  "entry": "bin/Orisumi.Observer.ForegroundState"
}
JSON

observers="$contents/Resources/Observers"
rm -rf "$observers"
mkdir -p "$observers"
# -X drops extra file attributes; links are followed, since Runtime rejects symbolic links in packages.
(cd "$staging" && zip -qrX "$observers/foreground-state-$version-$target.zip" .)
