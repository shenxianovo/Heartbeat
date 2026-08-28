# ADR-002: Event-Driven Foreground Window Tracking via WinEvent Hooks

## Status: Accepted

## Date: 2026-02-27

[`0ad53dc`](https://github.com/shenxianovo/heartbeat/commit/0ad53dc) — feat: implement event-driven foreground window tracking

## Context

The initial implementation used a **polling loop** (`Timer` + `GetForegroundWindow`) to detect which application is in the foreground. This had two problems:

1. **Wasted CPU**: Polling at 1-second intervals burns cycles even when the user hasn't switched apps.
2. **Missed transitions**: A fast Alt-Tab sequence (A → B → C within one polling interval) would miss B entirely.

Alternatives considered:

- **Lower polling interval** (e.g., 200ms): Better accuracy but higher CPU cost — unacceptable for a background agent.
- **WinEvent hooks** (`SetWinEventHook` with `EVENT_SYSTEM_FOREGROUND`): OS pushes events on every window activation. Zero cost when idle, no missed transitions.

## Decision

Replaced the polling timer with **WinEvent hooks** (`SetWinEventHook` for `EVENT_SYSTEM_FOREGROUND`, `EVENT_SYSTEM_MINIMIZESTART`, `EVENT_SYSTEM_MINIMIZEEND`).

The hook runs on a **dedicated background thread** with its own Win32 message loop (`GetMessage` / `DispatchMessage`), because `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT` requires a message pump on the calling thread.

## Consequences

- ✅ Zero CPU usage when no window switch occurs — ideal for a long-running background agent.
- ✅ Every foreground transition is captured, no matter how fast.
- ⚠️ Requires a dedicated thread with a Win32 message pump — adds platform-specific complexity.
- ⚠️ The `WinEventDelegate` must be stored as a field to prevent GC from collecting the callback (a subtle P/Invoke pitfall).
- ⚠️ Tightly coupled to Windows — no cross-platform path.

## References

- [`collection/desktop/Heartbeat.Desktop.Windows/Utils/WindowsWindowEventMonitor.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/Utils/WindowsWindowEventMonitor.cs) — 当前 WinEvent hook + message loop adapter
- [`collection/desktop/Heartbeat.Collector.System/Collection/AppMonitorService.cs`](../../collection/desktop/Heartbeat.Collector.System/Collection/AppMonitorService.cs) — hook thread lifecycle management
