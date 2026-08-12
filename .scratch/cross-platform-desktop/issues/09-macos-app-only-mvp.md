# 09 — Deliver the macOS App-only MVP

**What to build:** Deliver a useful Apple Silicon macOS Agent that records foreground App and hard away transitions without requesting Accessibility or Input Monitoring. It must use the shared hub, desktop semantics, and Avalonia experience while behaving as a lightweight menu-bar accessory application.

**Blocked by:** 06 — Resolve external Collector App hints; 08 — Replace WPF with Avalonia on Windows.

**Status:** ready-for-human

- [x] The macOS platform adapter reports frontmost App activation as normalized `mac:` AppIdentity keys using bundle identifiers, with the specified executable-identity fallback when a bundle identifier is unavailable.
- [x] Manual lock/session inactivity, display sleep, and system sleep immediately enter `sys:away`; the corresponding active/wake signals leave away without using a soft-idle timeout.
- [x] App and away transitions update Current Activity immediately and produce ActivitySegment snapshots with the same semantic rules as Windows.
- [x] The macOS Device uses IOPlatformUUID as its stable HardwareId and hostname only as the default DeviceName.
- [x] Heartbeat presence, AppIcon hints, versioned caches, Upload Streams, strict AppIdentity ingest, external Collector hints, authentication, and restart recovery work on macOS.
- [x] The application runs as a menu-bar accessory without a persistent Dock icon; the menu opens status/settings, closing a window hides it, and only explicit quit stops collection.
- [x] Login-start behavior is available through the shared desktop UI and resumes collection after the user signs in.
- [x] First launch and App-only operation request neither Accessibility nor Input Monitoring, and the UI truthfully shows the currently available observation depth.
- [ ] Desktop.Core scenario tests are reused for macOS semantics, thin adapter tests verify native-to-semantic translation, and real-device smoke tests cover App switching, lock, display sleep, system sleep, menu-bar lifecycle, login start, cache restart, and upload.

## Comments

### 2026-08-12 — App-only implementation complete; disruptive real-device smoke pending

- Added the `Heartbeat.Agent.Mac` native adapter and `Heartbeat.Desktop.Mac` Avalonia menu-bar platform head targeting `osx-arm64`.
- Reused `Hub.Core`, `Desktop.Core`, strict AppIdentity ingest, versioned caches, Upload Streams, presence, Collector App Hints, and the shared desktop UI.
- Added thin adapter/composition/platform-head tests. A real-device launch successfully observed App transitions (`mac:com.microsoft.vscode`, `mac:com.apple.dock`, and `mac:com.openai.codex`) and started loopback ingest on macOS without TCC prompts.
- Remaining human step: execute `desktop/Heartbeat.Desktop.Mac/SMOKE-TEST.md`, especially lock/display/system sleep, sign-out/login-start, authenticated offline cache recovery, and upload verification.
