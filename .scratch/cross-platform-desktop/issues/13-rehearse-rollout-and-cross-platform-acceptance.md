# 13 — Rehearse strict rollout and cross-platform acceptance

**What to build:** Prove the server-first strict-contract cutover and the complete Windows/macOS product as an operational release, including the difficult path where an old Windows client accumulates legacy cache, upgrades, migrates, and retransmits it. The rehearsal must establish that no permanent rejection loop, silent loss, or platform regression remains before enforcement.

**Blocked by:** 05 — Cut Windows collection over to the strict AppIdentity protocol; 06 — Resolve external Collector App hints; 08 — Replace WPF with Avalonia on Windows; 10 — Package and update the signed macOS arm64 MVP; 11 — Add macOS Accessibility title depth; 12 — Add macOS Interaction Signal and InputEvent Recording.

**Status:** ready-for-agent

- [ ] A releasable strict-contract Windows client is available before Analytics begins rejecting the legacy AppName contract.
- [ ] The expected rollout window and observed collection rate fit within configured segment and input-event cache capacity, with a documented operator check before enforcement.
- [ ] In a server-first rehearsal, an old client receives update-required responses, retains new activity locally without a retry storm, and clearly prompts for Update.
- [ ] After Update, the client atomically migrates legacy segment/input caches, preserves segment IdentityKey and raw historical input Codes, tags the correct CodeSets, and uploads all valid cached records exactly once semantically.
- [ ] Invalid legacy records are isolated as dead letters while valid neighbors continue, and restarting the Agent cannot reintroduce an endless rejection loop.
- [ ] Cross-platform acceptance proves Windows and macOS identities for the same product aggregate into one App across Report, Presence, Timeline/Replay, App details, Collector evidence, icons, and Matchers while raw AppIdentity facts remain visible where required.
- [ ] Windows regression coverage includes foreground/title/away collection, physical-key statistics, tray/Avalonia lifecycle, Collector management, login start, cache recovery, and Update.
- [ ] macOS real-device coverage includes App-only first launch, IOPlatformUUID stability, menu-bar lifecycle, lock/display/system sleep, Accessibility grant/denial/revocation, Input Monitoring, login start, signed installation, notarization, and Update.
- [ ] Release verification covers both platform artifacts, strict Analytics deployment ordering, observability for 426/migration/dead letters, rollback boundaries, and removal of any temporary rollout-only compatibility code.
- [ ] The full PostgreSQL integration, shared core scenario, Dashboard, Windows smoke, and macOS real-device suites pass against the release candidates.
