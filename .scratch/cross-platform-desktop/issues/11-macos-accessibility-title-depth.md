# 11 — Add macOS Accessibility title depth

**What to build:** Add an optional deeper macOS observation capability that records focused-window and raw title changes when Accessibility is enabled, while continuing App-only collection when permission is absent, denied, or revoked. The permission is requested only in response to the user enabling that capability.

**Blocked by:** 09 — Deliver the macOS App-only MVP.

**Status:** ready-for-agent

- [ ] Enabling title collection explicitly requests Accessibility permission; initial App-only startup and unrelated settings do not trigger the prompt.
- [ ] When authorized, the macOS adapter distinguishes AppIdentity activation, focused-window change, and same-window title change as separate semantic observations.
- [ ] AppIdentity activation and focused-window change always create the appropriate segment boundary, independent of Interaction Signal availability.
- [ ] Without Interaction Signal, same-window title changes are ignored rather than generating noisy segments; ticket 12 can later enable click-gated acceptance.
- [ ] Stored titles remain lossless platform observations, and display formatting continues to use the shared product-key-based Title Formatter layer.
- [ ] Denied, unavailable, or revoked Accessibility permission degrades immediately to App-only collection without stopping Heartbeat, cache draining, Current Activity, or away detection.
- [ ] The Avalonia UI shows the current title capability and permission state, offers a user-initiated recovery path, and does not claim title collection when it is unavailable.
- [ ] Desktop.Core tests cover focus/title semantics and degradation, adapter tests cover notification translation, and real-device smoke tests cover prompting, denial, grant, revocation, App switching, focused-window switching, and noisy title animation.
