# Cross-Platform Desktop Collection

Status: ready-for-agent

## Problem Statement

Heartbeat 的桌面 Collection 当前只能在 Windows 上运行。新的主要工作设备是 Mac，因此用户无法在 macOS 上持续记录前台 App、away、Current Activity、窗口标题与输入统计，也无法让 Windows 与 macOS 上的同一产品汇总为同一个 App。

当前桌面实现把可移植的 hub/上传能力、system 采集器状态机、Win32 adapter、WPF UI、Windows 自启动与更新逻辑装在同一个 Windows-only 边界中。现有 App 模型又把 Windows 进程名直接当作 App，输入码把 Windows VK 当作通用语义，本地离线缓存没有版本号且不能隔离永久坏记录。直接增加 macOS 分支会扩大条件编译、污染事实语义，并在严格协议升级时制造无法停止重传的毒丸缓存。

## Solution

Heartbeat 将桌面端重构为可移植的 hub 与桌面状态机核心、共享 Avalonia UI，以及独立的 Windows/macOS platform head 和 adapter。Windows 保持现有能力；macOS 先交付无需敏感权限的 App-only MVP，再增加 Accessibility 窗口标题、Interaction Signal 与 InputEvent Recording。

Analytics 将 App 定义为跨平台产品，并以 AppIdentity 保存平台可观测身份。Windows 与 macOS 的 VS Code、QQ 等身份显式映射到同一个 App。ActivitySegment 保存 AppIdentity，Report、Matcher、Replay、Current Activity 和详情查询经 AppIdentity 聚合到 App。

协议升级采用严格切换：服务端不长期兼容旧 `AppName` 契约；新版 Agent 在本地原子升级旧缓存，保留旧活动 IdentityKey，并把旧输入事件标记为 `windows-vk-v1`。上传流区分可重试失败与永久坏记录，通过 dead-letter 保证数据不静默蒸发。

桌面 UI 统一迁移到 Avalonia。macOS 作为菜单栏 accessory app 运行，不常驻 Dock，按功能启用时分别请求 Accessibility 与 Input Monitoring。拒绝权限时 Agent 继续以较浅观测深度工作。macOS 首发只支持 Apple Silicon，通过 GitHub Releases 提供无 Developer ID 签名/公证的每用户安装包，并保留 Velopack 自动更新；首次运行接受由用户在“隐私与安全性”中手动放行。

## User Stories

1. As a Heartbeat owner, I want Heartbeat to run on my Mac, so that my new workstation activity is no longer absent from my history.
2. As a Heartbeat owner, I want Windows collection to keep working during the cross-platform refactor, so that adding macOS does not regress my existing data.
3. As a Heartbeat owner, I want Windows and macOS to share the same system collection semantics, so that reports remain comparable across devices.
4. As a Heartbeat owner, I want each physical Windows PC and Mac to remain a separate Device, so that I can filter and compare machines honestly.
5. As a Heartbeat owner, I want macOS to use a stable native machine identifier, so that a normal app update does not create a new Device.
6. As a Heartbeat owner, I want the Mac MVP to record the foreground App, so that useful collection begins before optional permissions are granted.
7. As a Heartbeat owner, I want the Mac MVP to record explicit away periods, so that sleep and lock time are not attributed to the last App.
8. As a Heartbeat owner, I want manual lock, session inactivity, display sleep, and system sleep to enter `sys:away`, so that absence is represented immediately and consistently.
9. As a Heartbeat owner, I want Current Activity to update immediately on App and away transitions, so that the Dashboard reflects what the machine is doing now.
10. As a Heartbeat owner, I want Current Activity to identify the cross-platform App product, so that Windows and Mac display the same product name.
11. As a Heartbeat owner, I want the platform AppIdentity to remain available alongside the App product, so that the observed process or bundle is not lost.
12. As a Heartbeat owner, I want Windows and Mac identities for VS Code to map to one App, so that its time and history are aggregated.
13. As a Heartbeat owner, I want Windows and Mac identities for QQ and similar products to map to one App, so that reports do not split by operating system.
14. As a Heartbeat owner, I want unknown AppIdentity values to be retained without guessing, so that new applications never cause data loss or false merges.
15. As a server operator, I want unknown identities to create provisional Apps, so that I can explicitly classify them later.
16. As a server operator, I want to dry-run an App merge, so that I can inspect affected identities, knowledge, icons, and provisional Apps before changing global data.
17. As a server operator, I want App merges to be transactional and idempotent, so that retries cannot leave product identity half-migrated.
18. As a server operator, I want only configured admin identities to modify global App mappings, so that ordinary users cannot affect every Owner.
19. As a Heartbeat owner, I want App keys to be short and readable, so that Matchers and configuration remain understandable.
20. As a Heartbeat owner, I want App keys to gain a qualifier only when a real collision exists, so that vendor names do not add unnecessary noise.
21. As a Heartbeat owner, I want each App product to have a stable DisplayName, so that platform process names and bundle identifiers are not shown as labels.
22. As a Heartbeat owner, I want each App product to keep one icon per Owner, so that aggregated reports have a stable visual identity.
23. As a Heartbeat owner, I want a first valid product icon to remain stable across Windows and Mac uploads, so that platform clients do not repeatedly overwrite it.
24. As a Heartbeat owner, I want browser activity on Mac to associate with the same App as the system collector, so that Replay label upgrades still work.
25. As a collector author, I want to report a logical App hint rather than hard-code platform process names, so that one Collector works across operating systems.
26. As a Heartbeat owner, I want the hub platform resolver to turn Collector hints into AppIdentity keys, so that platform knowledge stays in platform adapters.
27. As a Heartbeat owner, I want window titles to be optional on macOS, so that App-only collection works without Accessibility permission.
28. As a Heartbeat owner, I want focused-window changes to be recorded when Accessibility is granted, so that meaningful navigation creates accurate segments.
29. As a Heartbeat owner, I want same-window title animation to be ignored when click gating is unavailable, so that terminals and players do not explode into noisy segments.
30. As a Heartbeat owner, I want title collection to remain lossless, so that stored titles are not rewritten by platform-specific heuristics.
31. As a Heartbeat owner, I want title display formatting to use cross-platform App keys, so that the same formatter works on Windows and Mac.
32. As a privacy-conscious owner, I want Interaction Signal separated from InputEvent Recording, so that title gating does not automatically persist my input events.
33. As a Heartbeat owner, I want Interaction Signal to remain local and ephemeral, so that it only records the most recent click time needed for noise control.
34. As a Heartbeat owner, I want InputEvent Recording to be independently enabled, so that I explicitly choose whether raw input events are stored and uploaded.
35. As a Heartbeat owner, I want keyboard statistics to use physical key positions, so that Windows and Mac contribute to one heatmap despite different layouts and native codes.
36. As a Heartbeat owner, I want new input events marked with `heartbeat-key-position-v1`, so that their interpretation is explicit and versioned.
37. As a Heartbeat owner, I want historical Windows input events marked as `windows-vk-v1`, so that old data remains truthful rather than being guessed into a new code set.
38. As a Heartbeat owner, I want legacy and new key codes projected into one heatmap, so that historical statistics remain useful after the migration.
39. As a Heartbeat owner, I want the Windows UI migrated to Avalonia, so that Windows and Mac share one desktop experience.
40. As a Heartbeat owner, I want closing the desktop window to hide it rather than stop collection, so that the Agent remains continuously active.
41. As a Mac user, I want Heartbeat to live in the menu bar without a permanent Dock icon, so that it behaves like a lightweight monitor.
42. As a Mac user, I want to open settings and status from the menu bar, so that I can inspect collection without a full-time window.
43. As a Mac user, I want permission status shown per capability, so that I understand whether App, title, Interaction Signal, or InputEvent collection is active.
44. As a Mac user, I want permission requests to happen only when I enable the corresponding capability, so that first launch does not demand unnecessary access.
45. As a Mac user, I want collection to continue after denying a permission, so that one unavailable capability does not disable the Agent.
46. As a Heartbeat owner, I want login-start behavior exposed through the shared desktop UI, so that collection can resume automatically after signing in.
47. As a Heartbeat owner, I want the desktop UI to show when the server requires an update, so that a strict protocol cutover is understandable.
48. As a Heartbeat owner, I want old segment caches upgraded locally after an Agent update, so that offline history can be uploaded under the new contract.
49. As a Heartbeat owner, I want old segment IdentityKey values preserved during cache migration, so that snapshots continue to satisfy the server identity guard.
50. As a Heartbeat owner, I want old input caches explicitly tagged as Windows VK, so that no historical key is silently reinterpreted.
51. As a Heartbeat owner, I want cache migration to create a recoverable backup, so that a failed upgrade cannot erase offline history.
52. As a Heartbeat owner, I want cache migration failures shown in the UI, so that data problems are visible and actionable.
53. As a Heartbeat owner, I want network errors, rate limits, and server failures retried, so that temporary outages do not lose activity.
54. As a Heartbeat owner, I want permanently invalid records isolated from valid records, so that one poison item cannot block the entire upload stream.
55. As a Heartbeat owner, I want dead-letter records preserved as inspectable JSON, so that rejected data does not silently disappear.
56. As a Heartbeat owner, I want an upgrade-required response to pause pointless retries, so that the Agent clearly waits for a compatible version.
57. As a Heartbeat owner, I want the server-first cutover to buffer old-client activity locally, so that a short upload interruption does not lose work.
58. As a release operator, I want the new Windows client available before enforcing the new protocol, so that users can update immediately.
59. As a release operator, I want cache capacity verified against the expected upgrade window, so that strict cutover cannot overflow local storage.
60. As a Mac user, I want an Apple Silicon package with explicit Gatekeeper instructions, so that I can install the unsigned build without giving up automatic updates.
61. As a Mac user, I want Heartbeat installed per user under `~/Applications`, so that updates normally do not require administrator privileges.
62. As a Mac user, I want updates delivered from GitHub Releases, so that Windows and Mac use the same release source.
63. As a Mac user, I want updates to preserve a stable bundle identifier and install location, so that permission continuity can be tested honestly across ordinary upgrades.
64. As a release operator, I want macOS packaging performed on a macOS runner, so that native product building, packaging, and release verification use Apple tooling.
65. As a developer, I want Windows and Mac platform adapters to translate native events into one semantic contract, so that the state machine is tested once.
66. As a developer, I want hub runtime code independent of desktop concepts, so that desktop and headless hub instances can reuse it safely.
67. As a developer, I want desktop state machines independent of operating-system APIs, so that platform adapters stay thin.
68. As a developer, I want the implementation delivered in stable phases, so that data-model, UI, and platform regressions can be diagnosed independently.

## Implementation Decisions

- Follow the accepted phased sequence: first extract portable cores without behavior change; then land AppIdentity, strict protocol, cache migration, and InputCode on Windows; then replace WPF with Avalonia; then ship the macOS App-only MVP; finally add title and input depth.
- Introduce a pure .NET `Heartbeat.Collection.Hub` module for loopback ingest, buffering, upload streams, durable cache, authentication clients, declaration uplink, Current Activity read model, and other reusable hub runtime behavior.
- Introduce a pure .NET `Heartbeat.Collector.System` module for the system Collector, ActivitySegment folding, away state, title-noise state machine, Interaction Signal abstraction, InputEvent buffering, and platform-neutral desktop configuration.
- Keep Windows and macOS native integrations inside separate platform heads. Composition roots select the appropriate adapters; shared capability modules contain no platform conditionals.
- Replace WPF with a shared Avalonia UI library and independent Windows/macOS platform heads. Each platform head hosts the Agent and UI in one process.
- The Windows head remains a tray application. The macOS head is a menu-bar accessory application without a persistent Dock icon. Closing the settings window hides it and does not stop the Agent.
- Keep unheaded hub deployments dependent only on `Heartbeat.Collection.Hub`; they must not reference desktop collection or UI modules.
- Redefine App as a cross-platform product with stable `Key` and `DisplayName`. Keys default to short product slugs and add qualifiers only to resolve real collisions.
- Add AppIdentity as the global platform-observed identity mapped many-to-one to App. Windows identities use normalized `win:` keys, macOS identities use normalized `mac:` bundle identifiers, and synthetic identities use `sys:` keys.
- ActivitySegment persists AppIdentity rather than App directly. Reports, Replay, Matcher readings, App detail queries, and other product views join through AppIdentity to App.
- Unknown AppIdentity values create a provisional App instead of being rejected or heuristically merged.
- Provide an admin-only App merge API authorized by configured JWT subjects. The operation supports dry-run, executes transactionally, is idempotent, rebinds identities, reconciles product icons, migrates authoritative App-key knowledge, and removes obsolete provisional Apps.
- Presence uploads `CurrentAppIdentityKey`. Analytics stores the current AppIdentity and returns App product identity plus the raw AppIdentity key where useful.
- Keep one AppIcon per Owner/App product. Identity-based uploads resolve to App; the first valid icon remains until an explicit refresh.
- External Collectors report logical App hints. The hub platform resolver maps those hints to AppIdentity keys; Collectors do not hard-code Windows and macOS identifiers.
- Replace the ambiguous foreground-window callback with semantic observations that distinguish AppIdentity activation, focused-window change, and same-window title change.
- Always split on AppIdentity activation and focused-window change. Apply click gating only to same-window title changes.
- On macOS, frontmost App observation works without sensitive permission. Accessibility enables focused-window and title observation. Without Input Monitoring, focused-window changes remain valid while same-window title changes are ignored.
- Define away from hard signals only: session lock/inactive, display sleep, and system sleep enter `sys:away`; corresponding active/wake signals exit it. Soft idle remains excluded.
- Separate Interaction Signal from InputEvent Recording. Interaction Signal is local-only recent-click state; InputEvent Recording persists and uploads events. They may share a native hook or macOS permission but have independent configuration and consequences.
- Introduce versioned `CodeSet` interpretation. New Windows and macOS keyboard events use `heartbeat-key-position-v1`; historical Windows events use `windows-vk-v1`. Codes represent physical key positions, not produced characters.
- Update keyboard projections to understand both code sets without rewriting historical input rows.
- Replace unversioned raw cache arrays with a versioned durable format and explicit old-persistence DTOs. Migration occurs before normal cache loading, creates a backup, validates the new file, and only then archives the old file.
- Segment cache migration converts legacy AppName to AppIdentityKey but preserves IdentityKey. Input cache migration preserves Code and adds `windows-vk-v1`.
- Keep the server ingest contract strict after cutover. Missing legacy fields receive an upgrade-required response rather than long-lived server normalization.
- Classify upload failures: network failures, recoverable authentication failures, 408, 429, and 5xx retry; 400/422 batches are split to isolate invalid records; invalid records move to durable dead-letter storage; 426 pauses the stream and surfaces a required update.
- Perform the strict rollout server first. Old clients temporarily accumulate local cache until the update is installed; the updated Agent migrates and retransmits it.
- Migrate App product knowledge authoritatively: App-related Strand Matchers, Muted Matchers, Recurrence Probes, Device Current Activity, and other canonical product references must retain meaning. Derived question caches are invalidated and regenerated; historical Recap prose remains unchanged.
- macOS uses `IOPlatformUUID` as its stable machine identity; Windows retains `MachineGuid`. The domain promises a platform-native stable machine identifier, not identical reset behavior across operating systems.
- macOS distribution is direct rather than Mac App Store. Initial builds target `osx-arm64`, use a stable bundle identifier, GitHub Releases, Velopack updates, and per-user installation under `~/Applications`, but intentionally omit Developer ID signing and notarization. First launch may require manual Gatekeeper approval; see ADR-039.
- Do not introduce a background daemon/UI split, a second long-lived UI implementation, or a nested hub topology.

## Testing Decisions

- Tests assert externally visible behavior and domain invariants rather than private class structure, native API call order, or implementation-specific threading.
- The primary test seam is `Heartbeat.Collector.System`: feed semantic platform observations and assert emitted ActivitySegment snapshots, Current Activity, away transitions, title gating, permission degradation, and terminal flush behavior.
- Reuse and elevate the existing AppMonitorService fake-window/fake-power/fake-clock scenario style as the shared Windows/macOS state-machine contract.
- Test `Heartbeat.Collection.Hub` upload behavior with real temporary cache files and a controllable HTTP transport. Cover version detection, atomic migration, backup preservation, compaction, retry classification, batch splitting, dead-letter output, 426 pause, and restart recovery.
- Reuse the existing UploadStream and JsonFileCache behavioral tests as prior art; retain the “batch does not evaporate” invariant across retries and dead-letter isolation.
- Test Analytics with the existing PostgreSQL integration fixture. Cover AppIdentity creation, provisional App creation, multi-identity product aggregation, strict ingest validation, ActivitySegment identity guard, presence projection, icon selection, Matcher migration, and App merge dry-run/commit/idempotency/authorization.
- Add end-to-end service scenarios proving `win:code` and `mac:com.microsoft.vscode` aggregate into one App report while remaining distinguishable AppIdentity facts.
- Test external Collector App hints through the hub resolver and Analytics association, including browser Replay/App detail behavior on both Windows and macOS identities.
- Test both InputCode sets in Analytics and Dashboard projections. A historical Windows VK event and a new physical-position event for the same key should contribute to the same displayed key without altering stored raw codes.
- Test Avalonia ViewModels and state presentation independently of native windows. Cover capability states, degraded permissions, update-required state, cache migration failure, dead-letter visibility, collector management, login-start state, and close-to-hide commands.
- Keep platform adapter tests thin: native callbacks or notification payloads are translated into semantic observations; shared state-machine outcomes belong to `Heartbeat.Collector.System` tests.
- Use macOS real-device smoke tests for TCC prompts, permission behavior across updates, session lock, screen sleep, system sleep, focused-window events, input monitoring, menu-bar lifecycle, login start, unsigned Gatekeeper approval, installation, and update. Do not mock undocumented operating-system internals as proof of correctness.
- Add release verification for both Windows and macOS artifacts, including stable channels, stable bundle identity, expected unsigned macOS packages, and update from the previous published version with legacy caches present.
- Keep all existing Windows behavior and server report regression suites green during the prefactor phase before changing contracts.

## Out of Scope

- Linux desktop support.
- Intel macOS or Universal Binary in the first release.
- Mac App Store distribution or App Sandbox support.
- Mobile, phone, diary, photo, or other non-desktop Collectors.
- Soft-idle inference based on arbitrary inactivity thresholds.
- Screenshot, screen recording, clipboard, typed text, mouse movement, or produced-character capture.
- Automatic product merging based on display names, process-name similarity, bundle-name similarity, or vendor heuristics.
- A full ordinary-user UI for managing global AppIdentity mappings; the first management surface is an admin API.
- A separate background daemon and IPC-connected desktop UI.
- Long-term WPF/Avalonia coexistence.
- Server-side legacy AppName compatibility after the strict cutover.
- Rewriting historical Windows VK events into guessed physical codes.
- Per-AppIdentity platform icons in the first implementation.
- Automatic semantic normalization of raw window titles in Collection.
- Changing the hub star topology or adding hub-to-hub forwarding.

## Further Notes

- This spec implements ADR-033, ADR-034, ADR-035, and ADR-039 and extends the existing decisions for GitHub Releases, InputEvent collection, away detection, title-noise control, stable segment identity, hub upload streams, and Device as observed subject.
- The feature is intentionally multi-session. Each implementation ticket should fit a fresh context window, declare blockers, and preserve a runnable or verifiable slice.
- The strict protocol rollout accepts a temporary upload interruption. Before cutover, the release must exist, cache capacity must cover the expected delay, and update-required state must be visible.
- Accessibility and Input Monitoring are observation-depth capabilities, not prerequisites for the Agent to run.
- AppIdentity mappings are global facts. App DisplayName and product icon are presentation concerns; platform observations remain immutable evidence.
