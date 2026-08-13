# macOS App-only smoke test

This smoke test covers Issue 09 only. It must not request Accessibility or Input Monitoring.

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
