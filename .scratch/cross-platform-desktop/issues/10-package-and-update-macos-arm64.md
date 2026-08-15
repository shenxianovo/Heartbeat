# 10 — Package and update the macOS arm64 MVP

**What to build:** Turn the macOS MVP into a directly distributable per-user application with a stable bundle identity and working self-update path. Releases must be reproducible on macOS CI and delivered from the same GitHub Releases source used by Windows. The initial Release is intentionally not Developer ID signed or notarized; users accept the documented Gatekeeper approval step.

**Blocked by:** 09 — Deliver the macOS App-only MVP.

**Status:** ready-for-human

- [x] The initial macOS Release targets Apple Silicon only and declares a stable bundle identifier that will be retained across ordinary Updates.
- [x] Release builds intentionally omit Developer ID signing and Apple notarization, and installation guidance explains the first-launch Gatekeeper approval step.
- [x] The application installs per user under `~/Applications` and normal installation and Update flows do not require administrator privileges.
- [x] Velopack produces the macOS Setup/Release metadata and applies Updates through the shared update lifecycle without stopping collection before an update is ready to apply.
- [x] GitHub Releases contains the expected macOS artifacts alongside Windows artifacts and supports the configured stable update channel.
- [x] macOS packaging does not require Apple Developer secrets or signing variables.
- [x] macOS packaging and release verification run on a macOS CI runner using Apple tooling.
- [x] Installation and Update preserve the bundle identifier and per-user location; permission continuity is treated as a real-device observation, not a guarantee.
- [ ] Release verification covers clean per-user installation, first launch, update discovery/download/application, relaunch, and continued collection on a clean Apple Silicon Mac.

## Comments

### 2026-08-13 — Implementation complete; final Release verification deferred

- Added the shared `Heartbeat.Desktop.Updater.Velopack` lifecycle used by Windows and macOS. Checks and downloads leave the Agent running; Apply is gated on `ReadyToApply`, schedules the updater before stopping the Agent, and remains retryable if scheduling fails.
- Added the Apple Silicon Velopack Release path with stable bundle identifier `com.shenxianovo.heartbeat` and channel `osx-arm64-stable`.
- The macOS Release job requires no Apple Developer credentials. It builds on a macOS runner, constrains the Velopack Setup to current-user installation, and verifies architecture, stable bundle identity, unsigned trust state, updater payload, per-user domain, and feed metadata.
- Locally generated and inspected an unsigned arm64 `.app`, Setup, portable zip, full package, and stable-channel metadata. No GitHub Release was created or modified.
- Per maintainer sequencing, the two remaining checks are deferred to Issue 13 after all implementation issues are complete: publish the combined Windows/macOS Release, then execute unsigned clean-machine installation, Gatekeeper approval, and Update acceptance.

### 2026-08-15 — Unsigned distribution accepted

- ADR-039 replaces the Developer ID/notarization requirement with an explicit unsigned distribution decision while retaining the Velopack automatic-update channel.
- CI no longer consumes Apple Developer certificates, identities, passwords, Team ID, keychain secrets, or notarization profiles.
- The final real-device acceptance must record Gatekeeper behavior and whether Accessibility/Input Monitoring remain granted or require reauthorization across a concrete `vA -> vB` update.
- A local Velopack 1.2.0 rehearsal produced unsigned Setup/Portable/full/feed artifacts, an arm64 `Heartbeat.app` with `UpdateMac` and `sq.version`, and a per-user installer. The app host reported an ad-hoc signature with no TeamIdentifier and the installer reported no signature, matching ADR-039. The Release workflow's same structural checks now pass locally.

### 2026-08-15 — v4.0.0 Release published

- The tag-first Release workflow completed successfully before the strict backend was pushed to `main`. Windows x64, Windows arm64, and unsigned macOS arm64 jobs all passed and published Setup, Portable, full/delta packages where a baseline existed, and stable-channel metadata to GitHub Release `v4.0.0`.
- The remaining clean-install/Update checkbox is deliberately open: CI proves package structure, while Gatekeeper approval, installed-app relaunch, and TCC behavior require the real `vA -> vB` device rehearsal in Issue 13.
