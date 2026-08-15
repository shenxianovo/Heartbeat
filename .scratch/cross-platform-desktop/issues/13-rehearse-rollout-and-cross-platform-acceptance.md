# 13 — Rehearse strict rollout and cross-platform acceptance

**What to build:** Prove the server-first strict-contract cutover and the complete Windows/macOS product as an operational release, including the difficult path where an old Windows client accumulates legacy cache, upgrades, migrates, and retransmits it. The rehearsal must establish that no permanent rejection loop, silent loss, or platform regression remains before enforcement.

**Blocked by:** 05 — Cut Windows collection over to the strict AppIdentity protocol; 06 — Resolve external Collector App hints; 08 — Replace WPF with Avalonia on Windows; 10 — Package and update the macOS arm64 MVP; 11 — Add macOS Accessibility title depth; 12 — Add macOS Interaction Signal and InputEvent Recording.

**Status:** ready-for-agent

- [ ] A releasable strict-contract Windows client is available before Analytics begins rejecting the legacy AppName contract.
- [ ] The expected rollout window and observed collection rate fit within configured segment and input-event cache capacity, with a documented operator check before enforcement.
- [ ] In a server-first rehearsal, an old client receives update-required responses, retains new activity locally without a retry storm, and clearly prompts for Update.
- [ ] After Update, the client atomically migrates legacy segment/input caches, preserves segment IdentityKey and raw historical input Codes, tags the correct CodeSets, and uploads all valid cached records exactly once semantically.
- [ ] Invalid legacy records are isolated as dead letters while valid neighbors continue, and restarting the Agent cannot reintroduce an endless rejection loop.
- [ ] Cross-platform acceptance proves Windows and macOS identities for the same product aggregate into one App across Report, Presence, Timeline/Replay, App details, Collector evidence, icons, and Matchers while raw AppIdentity facts remain visible where required.
- [ ] Windows regression coverage includes foreground/title/away collection, physical-key statistics, tray/Avalonia lifecycle, Collector management, login start, cache recovery, and Update.
- [ ] macOS real-device coverage includes App-only first launch, IOPlatformUUID stability, menu-bar lifecycle, lock/display/system sleep, Accessibility grant/denial/revocation, Input Monitoring, login start, unsigned installation and Gatekeeper approval, Velopack Update/relaunch, and observed permission behavior across the Update.
- [ ] Release verification covers both platform artifacts, strict Analytics deployment ordering, observability for 426/migration/dead letters, rollback boundaries, and removal of any temporary rollout-only compatibility code.
- [ ] The full PostgreSQL integration, shared core scenario, Dashboard, Windows smoke, and macOS real-device suites pass against the release candidates.

## Comments

### 2026-08-15 — Rehearsal started after implementation audit

- Direct implementation dependencies were re-audited on current HEAD. Issues 05 and 06 are fully implemented; Issues 08, 10, 11, and 12 have completed code/configuration and deliberately carry their remaining Windows/macOS smoke, unsigned Release, Gatekeeper, TCC, Input Monitoring, and Update checks into this final acceptance issue.
- Baseline verification passed 635 .NET tests, 138 Dashboard tests, and 75 Browser Collector tests; both TypeScript products built successfully. The desktop Release workflow contains Windows x64/arm64 artifacts and the unsigned, per-user macOS arm64 channel, but no external GitHub Release or clean-machine result is inferred from repository state.

### 2026-08-15 — macOS acceptance boundary revised

- ADR-039 accepts an unsigned/non-notarized macOS Release while retaining Velopack automatic updates. Apple Developer secrets and signing variables were removed from CI.
- Acceptance now requires evidence for manual Gatekeeper approval and a real `vA -> vB` update/relaunch. Accessibility and Input Monitoring continuity must be observed and recorded; it is not inferred from bundle identifier or Velopack behavior.
- The next phase is operational rather than another feature implementation: freeze a release candidate, document the cache-capacity/enforcement gate, rehearse the server-first 426 → update → migration → replay path, publish both platform artifacts, and record Windows/macOS real-device evidence.
- Before mutating deployment or creating a GitHub Release, resolve the maintainer's rollout questions and record the chosen release candidate version, test environment, expected upgrade window, rollback boundary, and authorized release/deployment actions here.

### 2026-08-15 — Local end-to-end and historical database audit

- The source-built `compose.local.yml` stack was healthy through `127.0.0.1:8080`: frontend and `/health` returned 200, PostgreSQL was healthy, all migrations were current, and App Catalog v2 started in `normal` mode. The backend emits a non-fatal missing `libgssapi_krb5.so.2` loader message before successfully using password authentication; this remains log noise to resolve, not evidence of a failed request path.
- A real macOS client remained running during the audit. Its segment cache was schema v2 and empty after draining; Window Activity, optional Interaction Signal, and InputEvent Recording were enabled. The Agent continued one-minute segment/input uploads, Presence updates succeeded, device 4 remained the same database Device, and no current 400/422/426/dead-letter loop was observed.
- The preserved database contains 182,437 ActivitySegments from 2026-02-26 through the audit and 1,003,471 InputEvents from 2026-06-29 through the audit. It contains four Devices and both historical Windows and current macOS observations; no invalid/future activity ranges, unknown/missing InputEvent CodeSets, negative input codes, or orphaned foreign keys were found.
- AppIdentity expansion is complete for all system segments. One historical browser segment remains without AppIdentity, which the plugin contract permits because association is optional. All Windows input rows are `windows-vk-v1`; all current Mac input rows are `heartbeat-key-position-v1`. Chrome, Feishu, Heartbeat, QQ, and VS Code each have Windows and macOS identities mapped to one App and have real segment history on both platforms.
- Historical quality findings are preserved rather than mutated: six exact duplicate system rows exist on device 2 (five on 2026-03-30, one on 2026-06-14); old system tracks double-count 55.992 hours on device 1 and 18.039 hours on device 2 because of overlaps, while devices 3 and 4 have zero system-track overlap. Browser tracks may overlap by design. Thirty-eight expanded Windows rows retain a stale legacy `AppId` after normalized-identity collision handling, but their authoritative `AppIdentityId -> App` mapping is valid and all product consumers use that path.
- Capacity was checked against observed history. The 20,000-segment cache covers at least 6.7 days at the worst observed system-segment day, but the 100,000-input cache covers only 2.5 days at the worst observed Windows input day (3.3 days on the other high-volume Windows device). Until capacity changes, the operational server-first rejection window should target no more than 48 hours; the checklist remains open until the maintainer accepts a rollout window.
- Local unsigned packaging and complete .NET regression passed: Velopack 1.2.0 generated Setup/Portable/full/feed artifacts and the expected updater payload, and all 635 .NET tests passed. Still outstanding are an actual combined GitHub Release, a real old-Windows-client 426/cache/migrate/replay rehearsal, Windows device smoke, and a real installed macOS `vA -> vB` Gatekeeper/TCC update rehearsal.
