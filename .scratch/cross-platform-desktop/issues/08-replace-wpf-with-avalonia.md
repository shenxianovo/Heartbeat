# 08 — Replace WPF with Avalonia on Windows

**What to build:** Replace the Windows-only WPF presentation with the shared Avalonia desktop experience while keeping the Windows Agent continuously active and preserving all operational controls. The Windows platform head remains one tray process hosting both Agent and UI, and WPF is removed rather than retained as a second long-lived implementation.

**Blocked by:** 01 — Extract the portable Collection Hub and system Collector; 05 — Cut Windows collection over to the strict AppIdentity protocol; 07 — Ship cross-platform physical-key statistics on Windows.

**Status:** ready-for-human

- [x] The Windows desktop experience runs through Avalonia and continues hosting the Agent and UI in one process.
- [x] The tray lifecycle, open-settings action, close-to-hide behavior, explicit quit behavior, and continuous background collection match the existing Windows product behavior.
- [x] Current Activity, authentication/API-key setup, Device status, Collector panel, Active/enabled states, system Collector read-only behavior, and login-start controls retain functional parity.
- [x] Update presentation preserves the Idle → UpdateAvailable → Downloading → ReadyToApply lifecycle and distinguishes UpToDate, UpdateFound, and CheckFailed results.
- [x] The UI presents update-required, cache-migration failure, dead-letter availability, and capability/configuration states supplied by the portable cores.
- [x] InputEvent Recording and Interaction Signal are presented as distinct settings with distinct consequences.
- [x] Avalonia ViewModel tests cover state presentation and commands without requiring native windows, including close-to-hide, Collector management, update application gating, and degraded/error states.
- [ ] A Windows smoke test verifies tray startup, window reopening, login start, collection while hidden, Update behavior, and clean explicit shutdown.
- [x] WPF is no longer shipped or maintained after Avalonia reaches parity, and Windows packaging/release behavior remains functional.

## Comments

### 2026-08-12 — Avalonia implementation complete; Windows smoke pending

- Added shared `Heartbeat.Desktop.UI` Avalonia presentation and native-window-free ViewModel tests.
- Added `Heartbeat.Desktop.Windows` as the one-process Windows tray platform head hosting Agent and UI.
- Removed `Heartbeat.WPF` and changed Windows Release publishing/Velopack entry point to `Heartbeat.Desktop.Windows.exe`.
- Desktop regression suites pass and self-contained `win-x64` / `win-arm64` publishes produce the new executable.
- Remaining Windows lifecycle verification is part of Issue 13 cross-platform acceptance.
