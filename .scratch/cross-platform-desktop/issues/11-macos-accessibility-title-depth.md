# 11 — Add macOS Accessibility title depth

**What to build:** Add an optional deeper macOS observation capability that records focused-window and raw title changes when Accessibility is enabled, while continuing App-only collection when permission is absent, denied, or revoked. The permission is requested only in response to the user enabling that capability.

**Blocked by:** 09 — Deliver the macOS App-only MVP.

**Status:** ready-for-human

- [x] Enabling title collection explicitly requests Accessibility permission; initial App-only startup and unrelated settings do not trigger the prompt.
- [x] When authorized, the macOS adapter distinguishes AppIdentity activation, focused-window change, and same-window title change as separate semantic observations.
- [x] AppIdentity activation and focused-window change always create the appropriate segment boundary, independent of Interaction Signal availability.
- [x] Without Interaction Signal, same-window title changes are ignored rather than generating noisy segments; ticket 12 can later enable click-gated acceptance.
- [x] Stored titles remain lossless platform observations, and display formatting continues to use the shared product-key-based Title Formatter layer.
- [x] Denied, unavailable, or revoked Accessibility permission degrades immediately to App-only collection without stopping Heartbeat, cache draining, Current Activity, or away detection.
- [x] The Avalonia UI shows the current title capability and permission state, offers a user-initiated recovery path, and does not claim title collection when it is unavailable.
- [ ] `Heartbeat.Collector.System` tests cover focus/title semantics and degradation, adapter tests cover notification translation, and real-device smoke tests cover prompting, denial, grant, revocation, App switching, focused-window switching, and noisy title animation.

## Comments

### 2026-08-14 — Accessibility title implementation complete; disruptive TCC smoke pending

- Added an opt-in `WindowTitleObservationEnabled` setting. Startup and unrelated settings only inspect current trust; only the explicit UI toggle calls `AXIsProcessTrustedWithOptions` with the prompt option.
- Added a dedicated AXObserver run loop for the frontmost process. Workspace activation, focused-window changes, and same-window title changes remain distinct semantic observations, and late callbacks from the previous process are discarded.
- Permission loss is polled and degrades the observer to App-only without stopping workspace App/away observations, the hub, cache draining, Current Activity, or uploads.
- The shared Avalonia Collector page now exposes the title toggle, truthful capability state, and an explicit path to macOS Accessibility settings when recovery is required.
- Shared system Collector, macOS adapter/state, and UI tests pass. The full .NET suite passes (601 tests), the product-key Title Formatter tests pass (6 tests), and a non-prompting real-device check successfully read/attached to the current focused window when already trusted.
- Remaining human smoke: start from disabled/clean TCC and confirm no launch prompt; enable and test prompt + denial; grant and test App switching/focused-window switching; exercise a same-window animated title with no Interaction Signal; revoke while running and confirm immediate App-only degradation; use the recovery action to regrant.
