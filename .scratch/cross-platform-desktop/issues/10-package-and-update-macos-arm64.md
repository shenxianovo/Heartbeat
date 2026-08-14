# 10 — Package and update the signed macOS arm64 MVP

**What to build:** Turn the macOS MVP into a directly distributable, trusted, per-user application with a stable identity and working self-update path. Releases must be reproducible on macOS CI and delivered from the same GitHub Releases source used by Windows.

**Blocked by:** 09 — Deliver the macOS App-only MVP.

**Status:** ready-for-human

- [x] The initial macOS Release targets Apple Silicon only and declares a stable bundle identifier that will be retained across ordinary Updates.
- [x] Release builds use Developer ID signing, hardened runtime settings, required entitlements, and Apple notarization so a clean Mac can launch them without an untrusted-app warning.
- [x] The application installs per user under `~/Applications` and normal installation and Update flows do not require administrator privileges.
- [x] Velopack produces the macOS Setup/Release metadata and applies Updates through the shared update lifecycle without stopping collection before an update is ready to apply.
- [ ] GitHub Releases contains the expected macOS artifacts alongside Windows artifacts and supports the configured stable update channel.
- [x] Signing and notarization secrets are consumed only by protected release automation and are not embedded in source or unsigned artifacts.
- [x] macOS packaging, signing, notarization, and release verification run on a macOS CI runner using Apple tooling.
- [x] Installation and Update preserve the bundle identity and code-signing identity needed for macOS permission continuity.
- [ ] Release verification covers clean per-user installation, first launch, update discovery/download/application, relaunch, and continued collection on a clean Apple Silicon Mac.

## Comments

### 2026-08-13 — Implementation complete; final Release verification deferred

- Added the shared `Heartbeat.Desktop.Updater.Velopack` lifecycle used by Windows and macOS. Checks and downloads leave the Agent running; Apply is gated on `ReadyToApply`, schedules the updater before stopping the Agent, and remains retryable if scheduling fails.
- Added the Apple Silicon Velopack Release path with stable bundle identifier `com.shenxianovo.heartbeat` and channel `osx-arm64-stable`.
- The protected macOS Release job imports ephemeral Developer ID credentials, signs with hardened-runtime entitlements, notarizes and staples the app, constrains the Velopack Setup to current-user installation, re-signs/re-notarizes it, and verifies identity, architecture, entitlements, Gatekeeper acceptance, per-user domain, and metadata.
- Locally generated and inspected an unsigned arm64 `.app`, Setup, portable zip, full package, and stable-channel metadata. No GitHub Release was created or modified.
- Per maintainer sequencing, the two remaining checks are deferred to Issue 13 after all implementation issues are complete: publish the combined Windows/macOS Release, then execute signed clean-machine installation and Update acceptance.
