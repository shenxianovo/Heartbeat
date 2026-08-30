# 01 — Extract the portable Collection Hub and system Collector

**What to build:** Preserve the current Windows Agent end to end while separating reusable hub runtime behavior and platform-neutral desktop collection semantics from the Windows adapters and UI. The resulting cores must be usable without Windows APIs, and an unheaded hub must not acquire desktop or UI dependencies.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The Windows Agent still collects system ActivitySegments, accepts loopback Collector segments, maintains Current Activity, sends Heartbeat presence, and drains both Upload Streams with no externally visible behavior regression.
- [ ] Hub runtime behavior can be composed without referencing desktop collection, Windows APIs, or a UI framework.
- [ ] Desktop collection behavior consumes semantic platform observations and emits ActivitySegment snapshots, Current Activity transitions, away transitions, Interaction Signal decisions, and terminal flushes without directly calling operating-system APIs.
- [ ] Windows-specific observation, power/session, input, startup, tray, and update integrations remain behind platform adapters and a Windows composition root.
- [ ] Shared core modules contain no operating-system conditionals; platform selection happens at composition boundaries.
- [ ] Scenario tests cover foreground App changes, focused-window changes, same-window title noise, entering and leaving away, Current Activity freshness, and final snapshot flushes through the platform-neutral seam.
- [ ] Existing Windows collection, hub, upload, and Analytics regression suites remain green before any protocol or data-model change lands.
