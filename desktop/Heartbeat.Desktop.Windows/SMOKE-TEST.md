# Windows desktop smoke test

Run this checklist on a clean Windows machine against a Release build or installed Setup.

## Build

```powershell
dotnet publish desktop/Heartbeat.Desktop.Windows/Heartbeat.Desktop.Windows.csproj `
  -c Release `
  -r win-x64 `
  --self-contained `
  -o publish
```

## Lifecycle and tray

- Launch `Heartbeat.Desktop.Windows.exe`; confirm one Heartbeat tray icon appears and no settings window opens automatically.
- Click the tray icon and confirm the Avalonia settings window opens.
- Close the settings window and confirm it hides while the process and Agent remain running.
- Switch foreground applications while hidden, reopen settings, and confirm Current Activity changed.
- Start a second copy and confirm only one Agent process continues running.
- Restart Explorer and confirm the tray icon is restored.

## Configuration and collection

- Save API key, Device name, and upload interval; restart and confirm the values persist.
- Toggle login start, sign out/in, and confirm the Agent starts and resumes collection.
- Confirm the system Collector is read-only and always active.
- Toggle an external Collector and confirm its next config fetch sees the new enabled state.
- Disable InputEvent Recording and confirm Interaction Signal remains described as local-only while durable input upload stops.

## Degraded states

- With a fixture cache that cannot migrate, confirm the recovery path is shown.
- With a fixture dead-letter file, confirm its count and path are shown.
- Point an old client at a strict server response and confirm update-required is shown without discarding queued data.

## Update and shutdown

- Check updates when current and confirm the UI reports UpToDate.
- Check through a failing transport and confirm CheckFailed is distinct from UpToDate.
- Publish a newer test Release and confirm `UpdateAvailable → Downloading → ReadyToApply`.
- Confirm Apply is disabled before ReadyToApply and enabled afterward.
- Apply the Update and confirm the Agent stops cleanly, restarts into the new version, and resumes collection.
- Use the tray Quit action and confirm the process, loopback listener, hooks, and Agent all stop cleanly.
