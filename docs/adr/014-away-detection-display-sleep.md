# ADR-014: Away Detection via Display-Off & Sleep (Hard Signals Only)

## Status: Accepted

## Date: 2026-06-30

## Context

`AppMonitorService` accumulates the current foreground app's duration from `_currentStart` to "now" with no awareness of whether the user is actually present. This pollutes every duration statistic:

- Leave the machine with Edge in the foreground for 3 hours → recorded as 3 hours of Edge usage.
- Close the lid and sleep for 8 hours → the process is suspended; on resume, the elapsed wall-clock gap gets attributed to whatever app was foreground at suspend time.

This is the single largest source of noise in the usage data, and it directly undermines the planned "daily Replay" feature: a replay timeline is only meaningful if "the user was away" is represented honestly rather than smeared into the last-used app.

### Scope decision: hard signals only

We deliberately **exclude soft idle** — "user present but no input" (watching video, reading, idle-thinking). Detecting it requires polling `GetLastInputInfo` with an arbitrary threshold, which brings false positives and endless "how long counts as idle" tuning. The accepted trade-off: **possible under-reporting** (watching a fullscreen video for 2 hours with the screen on is still counted as usage) **in exchange for zero false positives and a clean, event-driven implementation.**

We only react to deterministic, OS-broadcast signals:

1. **Display off** (`GUID_CONSOLE_DISPLAY_STATE` via `WM_POWERBROADCAST`) — the primary signal. Covers power-plan timeout, manual screen-off, and the screen-off that accompanies lock/sleep.
2. **System suspend/resume** (`PowerModeChanged.Suspend/Resume`) — a correctness backstop for sleep, where the process is frozen and event-arrival timestamps become unreliable (see below).
3. **Session lock/inactive and unlock/active** — manual lock immediately means the user is away, even if the display remains lit briefly. Windows may also surface this through the configured LockApp identity; macOS uses workspace session notifications.

Lock is a hard signal, not soft-idle inference. Platform adapters should use a reliable session inactive/active notification where available; process-name normalization remains a backstop rather than the primary cross-platform contract.

### Why display-off alone is not enough (the sleep problem)

For ordinary display-off the process keeps running, the message pump is alive, and the event arrives on time — closing the current segment at the event-arrival time is correct.

System sleep is different: it **suspends the entire process**. The message pump stops. On resume, any backlogged power event is processed at the *wake* time, not the *sleep* time. Naively closing the segment at "event arrival time" would attribute the whole sleep span to the pre-sleep app. Hence sleep needs its own signal (#2) so the segment can be closed at a trustworthy timestamp (sleep moment), not the wake moment.

### Why "away" must be a visible segment, not a gap

The server merges adjacent **same-app, head-to-tail (≤ `MergeTolerance` = 1s)** segments (`UsageService.SaveUsageAsync`, `UsageMerger`). So simply "closing" a VSCode segment when the screen turns off and opening a new VSCode segment on return would let the server **merge them back into one continuous VSCode span**, re-absorbing the away time. Cutting the segment alone is therefore insufficient — the away period must leave a trace in the data that cannot be merged away.

A literal gap (uploading nothing) is also rejected: a blank stretch on the timeline is **ambiguous** — it can't be distinguished from "the Agent wasn't running / crashed / PC was off". For a Replay, "I was away" is a meaningful event that should be explicit.

## Decision

Introduce a single **`away` state** in `AppMonitorService`, driven only by hard signals, and represent away time as an **explicit synthetic usage segment** reusing the existing upload/merge/cache pipeline.

### 1. Power monitor (Agent library, self-contained)

Add `IPowerMonitor` with a Windows implementation `WindowsPowerMonitor`, following the **exact pattern already used by `WindowsLowLevelInputHook` and `ActiveWindowHelper`**: a dedicated background thread that creates a **message-only window** (`HWND_MESSAGE`), registers `RegisterPowerSettingNotification(GUID_CONSOLE_DISPLAY_STATE)`, and runs its own `GetMessage` pump translating `WM_POWERBROADCAST` and suspend/resume into semantic events.

```csharp
public interface IPowerMonitor
{
    event Action? DisplayOff;   // screen turned off
    event Action? DisplayOn;    // screen turned on
    event Action? Suspend;      // system entering sleep
    event Action? Resume;       // system woke up
    void Start();
    void Stop();
}
```

Rationale for keeping it **in the Agent library** (not in the WPF host): the library already owns two self-built message-pump P/Invoke components, so a third is zero stylistic drift and zero new infrastructure pattern. It keeps the library host-agnostic, so the Console Runner still compiles and exercises it — preserving Runner's role as a **compile-time guard** that the library hasn't grown a hidden WPF dependency. Putting power logic in `App.xaml.cs` (which already has an `HwndSource`) was rejected as a "sweet trap": it would weld a core collection-layer signal onto the UI host and make it untestable, just as the collection layer is about to grow richer for Replay.

### 2. Away state machine in `AppMonitorService`

A single `_isAway` flag, entered/exited only by hard display, sleep or session signals:

- **Enter away** (first of `DisplayOff`, `Suspend` or `SessionInactive`; subsequent signals while already away are ignored):
  - Close the current real-app segment at the **signal-arrival `clock.UtcNow`** (for `Suspend`, this runs in the pre-suspend window, so it's ≈ the true sleep moment).
  - Record `_awayStart`. Set `_isAway = true`.
- **Exit away** (first of `DisplayOn`, `Resume` or `SessionActive`):
  - Emit an away segment `[_awayStart, clock.UtcNow]` with `AppName = AwayAppName`.
  - Set `_isAway = false`, start a fresh real-app segment from the current foreground app at "now".
- While `_isAway`, **no real-app accumulation and no new segments** — `OnForegroundChanged` and `GetAndClearUsages` must respect the flag so the away span cannot leak into a real app.
- Backstop: if `Suspend` never ran (process frozen too fast), `Resume` closes the away segment using `_awayStart` if set, otherwise the last-known activity time.

### 3. Away as a synthetic segment (no schema change)

The away period is uploaded as a normal system ActivitySegment with synthetic `AppIdentityKey = "sys:away"`:

- A distinct synthetic identity **naturally separates away from real apps** while mapping every platform to the same App product.
- Reuses the entire existing pipeline: `≥1s` minimum, `LocalCache`, offline retry, server-side `App` upsert. **No entity / migration / DTO / server changes.**
- The frontend filters the away App product out of app rankings and renders it as a distinct block on the timeline.

### 4. `LockApp` normalization (rename only, does NOT drive the state machine)

Some machines surface the lock screen as a foreground process named `LockApp`; others have no such process. `LockApp` segments are semantically "user not present" and should not appear as a real app.

- The away **state machine is driven by hard display, sleep and session signals** (process-name-independent).
- Separately, when recording a normal segment, if the foreground process name is in a configurable `AwayProcessNames` list (default `["LockApp"]`), that segment's `AppName` is **normalized to `AwayAppName`** — a rename only, it does **not** enter/trigger the away state.
- Net effect: whether away time came from a hard signal or from a lock-screen foreground identity, it surfaces as `sys:away`. Machines without such a process are unaffected.

### 5. Config

Add to `AgentConfig`:

- `AwayProcessNames: string[]` — default `["LockApp"]`. Foreground process names normalized to `AwayAppName`.

## Consequences

- ✅ Duration statistics stop absorbing away/sleep time; the data becomes trustworthy enough to build Replay on.
- ✅ Away (display-off, sleep, lock) is a single explicit, visible, queryable timeline block — and any *remaining* blank stretch now unambiguously means "Agent wasn't running", a useful health signal.
- ✅ Zero schema/server changes — fully reuses the usage upload/merge/cache pipeline; sentinel app name handles both "don't merge with real apps" and "do merge adjacent away".
- ✅ `WindowsPowerMonitor` matches the existing self-built-message-pump pattern; library stays host-agnostic; Runner remains a compile-time architecture guard.
- ✅ `_isAway` as a single explicit state is harder to leak than scattered per-callback timestamp math.
- ⚠️ Soft idle is intentionally not handled — screen-on-but-absent time (e.g. watching video) is still counted. Accepted.
- ⚠️ Hardcoding-free but empirical: `LockApp` is an observed process name, not an API contract; mitigated by making it a config list.
- ⚠️ `Suspend` has only a short pre-suspend execution window; if missed, away-start falls back to last-known activity time (slightly less precise sleep-start). Acceptable.
- ⚠️ The frontend must filter the sentinel `AwayAppName` from app rankings; an unfiltered consumer would show `"__away__"` as an app.

## References

<!-- Filled in as implementation lands -->

- `desktop/Heartbeat.Agent/Utils/IPowerMonitor.cs` — power signal abstraction (pending)
- `desktop/Heartbeat.Agent/Utils/WindowsPowerMonitor.cs` — message-only window + power-setting notification (pending)
- `desktop/Heartbeat.Agent/Services/AppMonitorService.cs` — `_isAway` state machine, away segment emission (pending)
- `desktop/Heartbeat.Agent/Models/AgentConfig.cs` — `AwayProcessNames` (pending)
- [ADR-002](./002-event-driven-window-tracking.md) — foreground window tracking this builds on
