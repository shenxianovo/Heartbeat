#!/bin/bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
package_root="${1:-$repo_root/.artifacts/desktop}"
case "$(uname -m)" in arm64) runtime=osx-arm64 ;; x86_64) runtime=osx-x64 ;; *) exit 2 ;; esac
mkdir -p "$package_root"
package_root="$(cd "$package_root" && pwd)"
staging="$(mktemp -d "$package_root/.package.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
bundle="$staging/Heartbeat Dev.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources" "$staging/heartbeat.iconset"
dotnet publish "$repo_root/src/Desktop/Heartbeat.Desktop.Mac" -c Release -r "$runtime" --self-contained true -o "$bundle/Contents/MacOS" --nologo
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$repo_root/assets/desktop-collector/macos.png" --out "$staging/heartbeat.iconset/icon_${size}x${size}.png" >/dev/null
  doubled=$((size * 2))
  sips -z "$doubled" "$doubled" "$repo_root/assets/desktop-collector/macos.png" --out "$staging/heartbeat.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$staging/heartbeat.iconset" -o "$bundle/Contents/Resources/heartbeat.icns"
cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.shenxianovo.heartbeat.desktop</string>
<key>CFBundleName</key><string>Heartbeat Dev</string>
<key>CFBundleDisplayName</key><string>Heartbeat Dev</string>
<key>CFBundleExecutable</key><string>Heartbeat.Desktop.Mac</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>CFBundleIconFile</key><string>heartbeat.icns</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
codesign --force --deep --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
# Only replace this script's named build artifact after publishing and signing succeed.
rm -rf "$package_root/Heartbeat Dev.app"
mv "$bundle" "$package_root/Heartbeat Dev.app"
printf '%s\n' "Local application: $package_root/Heartbeat Dev.app"
