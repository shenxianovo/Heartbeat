# 10 — Package and update the signed macOS arm64 MVP

**What to build:** Turn the macOS MVP into a directly distributable, trusted, per-user application with a stable identity and working self-update path. Releases must be reproducible on macOS CI and delivered from the same GitHub Releases source used by Windows.

**Blocked by:** 09 — Deliver the macOS App-only MVP.

**Status:** ready-for-agent

- [ ] The initial macOS Release targets Apple Silicon only and declares a stable bundle identifier that will be retained across ordinary Updates.
- [ ] Release builds use Developer ID signing, hardened runtime settings, required entitlements, and Apple notarization so a clean Mac can launch them without an untrusted-app warning.
- [ ] The application installs per user under `~/Applications` and normal installation and Update flows do not require administrator privileges.
- [ ] Velopack produces the macOS Setup/Release metadata and applies Updates through the shared update lifecycle without stopping collection before an update is ready to apply.
- [ ] GitHub Releases contains the expected macOS artifacts alongside Windows artifacts and supports the configured stable update channel.
- [ ] Signing and notarization secrets are consumed only by protected release automation and are not embedded in source or unsigned artifacts.
- [ ] macOS packaging, signing, notarization, and release verification run on a macOS CI runner using Apple tooling.
- [ ] Installation and Update preserve the bundle identity and code-signing identity needed for macOS permission continuity.
- [ ] Release verification covers clean per-user installation, first launch, update discovery/download/application, relaunch, and continued collection on a clean Apple Silicon Mac.
