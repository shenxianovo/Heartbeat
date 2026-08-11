# 12 — Add macOS Interaction Signal and InputEvent Recording

**What to build:** Add optional macOS interaction depth with two independently controlled consequences: local click evidence for title-noise gating and durable keyboard/mouse InputEvent Recording for statistics. Permission sharing at the native hook must not collapse these into one privacy setting.

**Blocked by:** 07 — Ship cross-platform physical-key statistics on Windows; 11 — Add macOS Accessibility title depth.

**Status:** ready-for-agent

- [ ] Enabling either capability requests Input Monitoring only when required, and the UI shows permission and enabled state separately for Interaction Signal and InputEvent Recording.
- [ ] Interaction Signal retains only the recent local click time needed for same-window title gating and is never persisted or uploaded.
- [ ] With Accessibility and Interaction Signal available, same-window title changes create boundaries only when they satisfy the approved click-gating rule; App activation and focused-window changes remain ungated.
- [ ] InputEvent Recording can be enabled or disabled independently; when disabled, no keyboard or mouse events are written to cache or uploaded even if the native hook is active for Interaction Signal.
- [ ] macOS key-down events map to `heartbeat-key-position-v1` physical positions, while mouse button and wheel events use the shared cross-platform semantics.
- [ ] Key-up events are used only to suppress long-press auto-repeat and are not persisted, and event Ids retain UUIDv7 replay idempotency.
- [ ] Permission denial or revocation disables only the affected interaction/input depth and leaves App, away, Current Activity, title focus transitions, Heartbeat, and uploads functioning at their available depth.
- [ ] Analytics and Keyboard Heatmap combine macOS physical-position events with Windows legacy/new CodeSets without exposing raw event sequences.
- [ ] Tests cover the independent setting matrix, click gating, recording-off guarantees, key mapping, mouse semantics, retry/restart behavior, permission degradation, and real-device Input Monitoring persistence across an Update.
