#!/bin/sh
# Xcode build phase: publishes the foreground state helper into Contents/Helpers and signs it.
set -eu

export PATH="/usr/local/share/dotnet:$HOME/.dotnet:/opt/homebrew/bin:$PATH"
# The physical path: MSBuild loses project references when one project is reached through a symlink, such as /tmp.
repository="$(cd "$SRCROOT/../../.." && pwd -P)"
helpers="$TARGET_BUILD_DIR/$CONTENTS_FOLDER_PATH/Helpers"

case "$(uname -m)" in
  arm64) runtime=osx-arm64 ;;
  *) runtime=osx-x64 ;;
esac

# MSBuild reads environment variables as properties (Xcode exports TARGETNAME, CONFIGURATION, ...), so publish
# with a clean environment. Build output stays in DerivedData, so a running AppHost watch here does not restart.
env -i HOME="$HOME" PATH="$PATH" TMPDIR="${TMPDIR:-/tmp}" USER="${USER:-}" dotnet publish "$repository/src/Observers/Orisumi.Observer.ForegroundState" \
  --configuration Release --runtime "$runtime" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  --artifacts-path "$DERIVED_FILE_DIR/dotnet" --output "$helpers"

codesign --force --sign "${EXPANDED_CODE_SIGN_IDENTITY:--}" "$helpers/Orisumi.Observer.ForegroundState"
