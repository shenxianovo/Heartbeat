#!/bin/sh
# Xcode build phase: builds Runtime and the bundled Observer packages with orisumi observers pack, signed,
# and copies them into Contents/Helpers/Runtime and Contents/Resources/Observers.
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
# clean environment. The checkout's own developer tool runs, so the build does not depend on an installed orisumi.
packed="$DERIVED_FILE_DIR/observers"
cd "$repository"
env -i HOME="$HOME" PATH="$PATH" TMPDIR="${TMPDIR:-/tmp}" USER="${USER:-}" \
  dotnet run --project "$repository/tools/Orisumi.Dev" -- \
  observers pack foreground-state assistant-sessions --target "$target" --output "$packed" --sign "$sign"

rm -rf "$contents/Helpers/Runtime" "$contents/Resources/Observers"
mkdir -p "$contents/Helpers" "$contents/Resources"
cp -R "$packed/runtime" "$contents/Helpers/Runtime"
cp -R "$packed/bundled" "$contents/Resources/Observers"
