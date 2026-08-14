# ADR-003: Adopt .NET Generic Host for Desktop Client Lifecycle

## Status: Accepted

## Date: 2026-03-03

[`b851b7c`](https://github.com/shenxianovo/heartbeat/commit/b851b7c) — feat: refactor app monitor and upload service, add .NET general host

## Context

The original console client managed its own lifecycle: manual `Timer` setup, hand-wired service instantiation, and ad-hoc shutdown handling. As the agent grew (monitor service, usage upload, status upload, icon upload), this became fragile:

- No unified DI container — services were manually newed up and passed around.
- No graceful shutdown — the agent could lose in-flight usage data on `Ctrl+C`.
- Adding a WPF host later would mean duplicating all the wiring code.

Alternatives considered:

1. **Keep manual wiring**: Simpler for a small console app, but doesn't scale as services multiply.
2. **.NET Generic Host** (`Microsoft.Extensions.Hosting`): Provides DI, configuration, `IHostedService` / `BackgroundService`, and `CancellationToken`-based graceful shutdown out of the box.

## Decision

Adopted **.NET Generic Host**. The monitoring service implements `IHostedService`; periodic upload tasks use `BackgroundService`. All services are registered via DI. The host handles `Ctrl+C` / `SIGTERM` gracefully, flushing pending uploads on shutdown.

## Consequences

- ✅ Clean DI: services declare dependencies via constructor injection, no manual wiring.
- ✅ Graceful shutdown: `UsageUploadWorker.StopAsync` flushes remaining data before exit.
- ✅ Reusable: the same service registrations later powered both Console Runner and WPF host (see [ADR-005](./005-extract-agent-library.md)).
- ⚠️ Heavier startup for what was originally a 50-line console app.
- ⚠️ Developers must understand the `IHostedService` lifecycle (Start → Run → Stop ordering).

## References

- [`collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/Hosting/AgentHostExtensions.cs) — service registration
- [`collection/desktop/Heartbeat.Desktop.Windows/Workers/UsageUploadWorker.cs`](../../collection/desktop/Heartbeat.Desktop.Windows/Workers/UsageUploadWorker.cs) — `BackgroundService` with graceful flush
- [`collection/hub/Heartbeat.Collection.Hub/Runtime/StatusUploadWorker.cs`](../../collection/hub/Heartbeat.Collection.Hub/Runtime/StatusUploadWorker.cs) — status heartbeat worker
- `Heartbeat.Agent.Runner` — historical console host, retired by ADR-037
