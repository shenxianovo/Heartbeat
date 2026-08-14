# ADR-005: Extract Heartbeat.Agent as Reusable Class Library

## Status: Superseded by ADR-037

## Date: 2026-03-18

[`8bc6966`](https://github.com/shenxianovo/heartbeat/commit/8bc6966) — refactor(client): extract monitoring service to Heartbeat.Agent

## Context

After adding the WPF project (`Heartbeat.WPF`) alongside the existing console runner (`Heartbeat.Agent.Runner`), both hosts needed the same core logic: window monitoring, usage upload, status upload, icon extraction, and local caching.

Without extraction, we faced two options:

1. **Duplicate the code** in both projects: Fast to set up, but any bug fix or feature change must be applied twice.
2. **Extract a shared class library** (`Heartbeat.Agent`): Both hosts reference one library and call a single `AddHeartbeatAgent()` extension method. Changes propagate automatically.

The challenge: the Agent library must be **host-agnostic** — it can't depend on WPF or console specifics. Configuration and instance management (e.g., `ConfigManager`, `SingleInstanceGuard`) must be injectable from the host.

## Decision

Extracted all monitoring and upload logic into **`Heartbeat.Agent`** (a .NET class library). Hosts register services via `IServiceCollection.AddHeartbeatAgent()`, optionally passing pre-created instances (e.g., WPF passes its own `ConfigManager` for UI binding).

The library owns:
- `AppMonitorService` (IHostedService)
- `UsageUploadWorker` / `StatusUploadWorker` (BackgroundService)
- `UsageUploadService` / `IconUploadService` / `StatusUploadService`
- `LocalCache`, `ActiveWindowHelper`, `IconHelper`
- `ConfigManager` (with file-based persistence)

## Consequences

- ✅ Single source of truth: bug fixes and features apply to both Console Runner and WPF simultaneously.
- ✅ Clean host boundary: hosts only configure and start the Generic Host; all agent logic lives in the library.
- ✅ Easy to add new hosts (e.g., a Windows Service) — just reference `Heartbeat.Agent` and call `AddHeartbeatAgent()`.
- ⚠️ The `ConfigManager` injection pattern (external instance vs. internal default) adds API surface complexity.
- ⚠️ The library targets `net10.0-windows` — not portable to other platforms due to Win32 P/Invoke dependencies.

## References

- [`collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs) — `AddHeartbeatAgent()` registration
- [`collection/desktop/Heartbeat.Desktop.Windows/Configuration/ConfigManager.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/Configuration/ConfigManager.cs) — injectable config manager
- `Heartbeat.Agent.Runner` — historical console host, retired by ADR-037
- [`collection/desktop/Heartbeat.Desktop.Windows/WindowsDesktopRuntime.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/WindowsDesktopRuntime.cs) — current Windows platform head using Agent
