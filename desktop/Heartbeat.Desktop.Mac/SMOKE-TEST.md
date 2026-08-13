# macOS smoke test

The App-only sections cover Issue 09 and must not request Accessibility or Input Monitoring. The Release sections cover Issue 10 and should be run only after all implementation issues are complete and a signed candidate has been published for end-to-end verification.

For UI inspection, set `HEARTBEAT_SHOW_SETTINGS_ON_START=1` to open the window on launch.
Set `HEARTBEAT_THEME_OVERRIDE=Light|Dark|System` to inspect a theme without changing user config.

## Start and menu-bar lifecycle

1. Run `dotnet run --project desktop/Heartbeat.Desktop.Mac/Heartbeat.Desktop.Mac.csproj`.
2. Confirm Heartbeat appears in the menu bar and has no persistent Dock icon.
3. Open settings from the menu bar. Confirm the capability list says:
   - foreground App: available;
   - window/title, Interaction Signal, and InputEvent Recording: unavailable;
   - App-only needs neither Accessibility nor Input Monitoring.
4. Close the settings window, switch between at least three applications, then reopen settings.
   Collection must continue while the window is hidden.
5. Choose **退出 Heartbeat**. Confirm the process and local ingest listener stop cleanly.

Logs and config are under `~/Library/Application Support/Heartbeat/`.

## App, Current Activity, and restart recovery

1. Configure a valid API key and a recognizable Device name.
2. Switch between an application with a bundle identifier and, if available, an unbundled executable.
3. Confirm logs show normalized `mac:<bundle-id>` identities and `mac:exe.<executable>` only as the fallback.
4. Confirm Dashboard Current Activity changes immediately and the Device uses IOPlatformUUID as HardwareId.
5. Disconnect the network, switch applications for several minutes, quit, restart, reconnect, and wait for a drain.
6. Confirm cached segments upload once, no valid segment enters dead letter, Heartbeat presence resumes, and App icons appear where the bundle supplies one.
7. Send a browser Collector segment using an App Hint and confirm it associates with the same App product as the system segment.

## Hard-away signals

For every case below, note the timestamp, stay away for at least five seconds, resume, and verify one `sys:away` span followed by the current foreground App:

1. Lock the session, then unlock it.
2. Let the display sleep (or use the normal display-sleep control), then wake it.
3. Put the Mac to system sleep, then wake it.
4. Repeat with two signals overlapping, such as lock followed by system sleep. There must still be one away span, not nested or duplicated spans.

Do not use a soft-idle timeout as a substitute for these checks.

## Login start

1. Enable login start in settings and confirm `~/Library/LaunchAgents/com.shenxianovo.heartbeat.plist` exists.
2. Sign out and back in.
3. Confirm Heartbeat returns as a menu-bar accessory, collection resumes, and no admin prompt appears.
4. Disable login start and confirm the LaunchAgent file is removed.

## Permission regression

After first launch and all App-only checks, confirm Heartbeat has not appeared as a newly requested app in either Accessibility or Input Monitoring privacy settings. No TCC prompt should have appeared.

## Signed Release artifacts

Run these checks against the GitHub Release candidate on a clean Apple Silicon Mac:

1. Confirm the Release contains `Heartbeat-osx-arm64-stable-Setup.pkg`, `Heartbeat-osx-arm64-stable-Portable.zip`, a full `.nupkg`, `releases.osx-arm64-stable.json`, and `RELEASES-osx-arm64-stable` alongside the Windows artifacts.
2. Run `pkgutil --check-signature Heartbeat-osx-arm64-stable-Setup.pkg` and confirm a Developer ID Installer identity.
3. Run `spctl --assess --type install --verbose=2 Heartbeat-osx-arm64-stable-Setup.pkg` and `xcrun stapler validate Heartbeat-osx-arm64-stable-Setup.pkg`; both must pass.
4. Install for the current user. Confirm the app is under `~/Applications/Heartbeat.app` and installation does not request administrator credentials.
5. Confirm `CFBundleIdentifier` is `com.shenxianovo.heartbeat`, the main executable is arm64, and `codesign --verify --deep --strict --verbose=2 ~/Applications/Heartbeat.app` passes.
6. Run `spctl --assess --type execute --verbose=2 ~/Applications/Heartbeat.app` and `xcrun stapler validate ~/Applications/Heartbeat.app`; both must pass.
7. Launch from Finder and confirm Gatekeeper shows no untrusted-app warning.

Record the bundle identifier and the Developer ID Application authority; the Update check below must produce the same values.

## Update and permission continuity

1. Install the previous signed stable version for the current user and complete the App-only checks above while collection is active.
2. Publish a newer signed candidate to the `osx-arm64-stable` channel.
3. Check for updates and confirm `UpdateAvailable → Downloading → ReadyToApply` while Current Activity and segment collection continue changing.
4. Confirm Apply remains unavailable before `ReadyToApply`.
5. Apply the Update. Confirm the Agent stops only at this point, the app relaunches into the new version, and collection resumes.
6. Confirm the updated app remains under `~/Applications`, no administrator prompt appeared, and its bundle identifier and Developer ID Application authority match the previous version.
7. Confirm login start still points into the same bundle, App-only operation still produces no new TCC prompt, existing settings/caches remain readable, and queued segments upload once after relaunch.
