# 12 — Add macOS interaction depth and shared System capability controls

**What to build:** Add optional macOS interaction depth with two independently controlled consequences: local click evidence for title-noise gating and durable keyboard/mouse InputEvent Recording for statistics. Reshape the shared Collector page so `system` owns its cross-platform capability controls and each capability separates user intent from effective permission/availability state. Permission sharing at the native hook must not collapse Interaction Signal and InputEvent Recording into one privacy setting.

**Blocked by:** 07 — Ship cross-platform physical-key statistics on Windows; 11 — Add macOS Accessibility title depth.

**Status:** ready-for-human

- [x] The `system` Collector is an expandable row with no master enable switch. Foreground App collection remains the always-on baseline; the collapsed row summarizes enabled optional capabilities and prioritizes actionable permission/failure state.
- [x] Remove the detached global capability card. The expanded `system` row owns four capability rows: Foreground App, Window Activity, Click-assisted title gating, and InputEvent Recording; each row colocates its control, effective state, explanation, and recovery action.
- [x] Window Activity is one optional capability covering focused-window transitions and raw title observation on both platforms. It defaults enabled when migrating/currently running on Windows and remains opt-in on macOS.
- [x] Interaction Signal is presented as the optional fallback “点击辅助判断” on Windows and macOS, with static guidance that it helps when no dedicated Collector is available and that it is never persisted or uploaded. Do not infer Collector-to-App coverage or add dynamic recommendations in this issue.
- [x] Interaction Signal defaults enabled on Windows to preserve existing behavior and disabled on macOS until the user opts in. When Window Activity is disabled, retain the user's Interaction Signal intent but report it paused and do not run a hook solely for that paused capability.
- [x] A capability toggle represents requested intent, not effective availability. Permission denial or revocation leaves the toggle enabled, reports the blocked state separately, and automatically resumes collection after permission recovery.
- [x] Enabling either capability requests Input Monitoring only when required, and the UI shows permission and enabled state separately for Interaction Signal and InputEvent Recording.
- [x] The shared native input hook runs while at least one effective consumer requires it and stops when neither does. Disabling either consumer does not affect the other, and only enabled InputEvent Recording may write durable input or upload it.
- [x] Interaction Signal retains only the recent local click time needed for same-window title gating and is never persisted or uploaded.
- [x] With Accessibility and Interaction Signal available, same-window title changes create boundaries only when they satisfy the approved click-gating rule; App activation and focused-window changes remain ungated.
- [x] InputEvent Recording can be enabled or disabled independently; when disabled, no keyboard or mouse events are written to cache or uploaded even if the native hook is active for Interaction Signal.
- [x] macOS key-down events map to `heartbeat-key-position-v1` physical positions, while mouse button and wheel events use the shared cross-platform semantics.
- [x] Key-up events are used only to suppress long-press auto-repeat and are not persisted, and event Ids retain UUIDv7 replay idempotency.
- [x] Permission denial or revocation disables only the affected interaction/input depth and leaves App, away, Current Activity, title focus transitions, Heartbeat, and uploads functioning at their available depth.
- [x] Analytics and Keyboard Heatmap combine macOS physical-position events with Windows legacy/new CodeSets without exposing raw event sequences.
- [ ] Tests cover the independent setting matrix, click gating, recording-off guarantees, key mapping, mouse semantics, retry/restart behavior, permission degradation, and real-device Input Monitoring persistence across an Update.

## Comments

### 2026-08-15 — System capability UI design grill

- `system` is an always-running Collector, not a user-toggleable source. Foreground App collection is its fixed baseline; all deeper observation is presented as owned capability state inside the expandable `system` row.
- Window Activity deliberately combines focused-window transitions and raw titles. Interaction Signal remains a separate, optional fallback for same-window title gating rather than a substitute for dedicated Collectors.
- Windows and macOS share the capability information architecture and independent controls while retaining platform-appropriate defaults and permission behavior.
- Requested intent, permission/availability, dependencies, and effective runtime state are separate. Input Monitoring and the native hook may be shared, but persistence remains gated solely by InputEvent Recording.
- Dynamic advice based on dedicated Collector coverage is deferred because the registry does not yet declare which Apps a Collector completely covers.

### 2026-08-15 — Implementation ready for human verification

- The shared Collector presentation now models `system` as an expandable owner of four capability rows. Requested intent, effective availability, dependency pauses, and permission recovery are separate state.
- Windows now exposes independent Window Activity, Click-assisted title gating, and InputEvent Recording settings while preserving all three existing behaviors as enabled defaults.
- macOS now has an explicit-action-only Input Monitoring permission flow, one passive CoreGraphics hook shared by the two input consumers, physical-key mapping, mouse-button normalization, and both traditional-wheel and continuous-trackpad scroll normalization.
- Durable input continues through the existing versioned cache and `UploadStream`; its retry/restart, replay-ID, legacy CodeSet projection, and heatmap contracts remain covered by the portable tests.
- Automated verification: `dotnet test Heartbeat.slnx --no-build --no-restore` passed 635 tests; frontend Vitest passed 138 tests; Vue typecheck and production build passed. Existing NU1903 advisories for Microsoft.OpenApi and SSH.NET remain unrelated.
- Human verification remains for the final checklist item: exercise Input Monitoring grant/denial/revocation with real keyboard, mouse, and trackpad input, then confirm the authorization and enabled intent survive an installed Velopack Update.

### 2026-08-15 — Shared settings controls and permission recovery follow-up

- Added Avalonia-native `SettingsCard` and `SettingsExpander` controls following the CommunityToolkit SettingsControls information architecture. The Collector page uses an Expander for `system` and nested Cards for capabilities; the Settings page uses the same Card/Expander vocabulary for connection, general, update, and diagnostic settings.
- The controls own full-width layout, shared icon/title/description/action slots, expansion, chevrons, and neutral non-selected visuals. Headless Avalonia regression tests cover full-width Collector and Settings layouts, capability-card composition, and the absence of a persistent selected-blue state.
- Permission-required capability rows now explain how to add Heartbeat when it is absent from the macOS privacy list. “去授权” first reveals the exact running `.app` or development executable in Finder and then opens the appropriate System Settings pane; “显示位置” remains available as an explicit recovery action.
