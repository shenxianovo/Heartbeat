# ADR-008: Local JSON Cache with Offline Retry for Usage Upload

## Status: Accepted；implementation shape amended by [ADR-020](./020-system-collector-through-hub.md) and [ADR-035](./035-strict-ingest-contract-and-versioned-cache-migration.md)

> 本 ADR 的持久离线重试不变量仍有效；下文的 `LocalCache`、`InputEventLocalCache`、
> `UsageUploadService` 与 `UsageUploadWorker` 是历史实现。当前实现已统一到版本化
> `JsonFileCache`、`UploadStream` 与 `UploadWorker`，不再维护两套平行 cache/service。

## Date: 2026-03-03

[`b851b7c`](https://github.com/shenxianovo/heartbeat/commit/b851b7c) — feat: refactor app monitor and upload service, add .NET general host
[`8bc6966`](https://github.com/shenxianovo/heartbeat/commit/8bc6966) — refactor(client): extract monitoring service to Heartbeat.Agent

## Context

The desktop agent runs on a personal PC that may lose network connectivity (Wi-Fi drops, VPN disconnects, laptop sleep). If an upload fails and the data is discarded, that usage window is permanently lost.

We needed a strategy to **buffer failed uploads** and retry them later. Options considered:

1. **In-memory queue only**: Simple, but data is lost if the agent process exits (crash, reboot, user closes it).
2. **SQLite local database**: Durable and queryable, but heavy dependency for what's essentially a FIFO buffer.
3. **Local JSON file**: Lightweight, human-readable for debugging, and sufficient for the expected data volume.

## Decision

Adopted a **local JSON file cache** (`LocalCache`):

- On upload failure (network error or non-2xx response), the usage records are appended to a JSON file in `%LocalAppData%/Heartbeat/cache.json`.
- On the next upload cycle, cached records are loaded and uploaded first (before fresh records).
- On successful cache upload, the file is cleared.
- The cache is capped at **10,000 records** — oldest entries are dropped if the cap is exceeded (prevents unbounded disk growth during extended outages).
- File writes use **atomic rename** (`write .tmp` → `rename`) to prevent corruption from mid-write crashes.
- Concurrent access is protected by `ReaderWriterLockSlim`.

## Consequences

- ✅ No data loss during temporary network outages — records survive agent restarts.
- ✅ Simple implementation: ~100 lines, no external dependencies.
- ✅ Human-readable: developers can inspect `cache.json` to debug upload issues.
- ⚠️ 10K record cap means extended outages (days) will lose the oldest data.
- ⚠️ JSON serialization on every failure — acceptable for the expected failure frequency (rare).
- ⚠️ No deduplication: if the server received the data but the response was lost, records may be uploaded twice (server-side merge handles this, see [ADR-001](./001-server-side-usage-merging.md)).

## Update (ADR-012): second cache for input events

The same offline-retry pattern was reused for raw input events, but as a **separate** cache file
and class (`InputEventLocalCache`) rather than extending `LocalCache`:

- Stored at `%LocalAppData%/Heartbeat/input-events-cache.json`, same atomic-rename + `ReaderWriterLockSlim` discipline.
- **Append-only, no merging** — input events are deduplicated server-side by their client-generated `Id` (UUIDv7), so the merge step `LocalCache` performs for usage records does not apply.
- Capped at **100,000 records** (vs. 10K for usage), since input events arrive at much higher volume (~50k/day); oldest dropped on overflow.
- Retried by the same `UsageUploadWorker` loop (`inputUploadService.UploadCachedAsync()` runs alongside the usage cache flush).

## References

- `collection/desktop/Heartbeat.Desktop.Windows/{Storage,Services,Workers}/...` — 本 ADR 两个提交时的
  历史实现路径，已由 ADR-020/035 的统一流替代
- [`JsonFileCache.cs`](../../collection/hub/Heartbeat.Collection.Hub/Storage/JsonFileCache.cs) 与
  [`HeartbeatCacheFormats.cs`](../../collection/hub/Heartbeat.Collection.Hub/Storage/HeartbeatCacheFormats.cs) — 当前版本化 cache 与迁移
- [`UploadStream.cs`](../../collection/hub/Heartbeat.Collection.Hub/Upload/UploadStream.cs) 与
  [`UploadWorker.cs`](../../collection/hub/Heartbeat.Collection.Hub/Runtime/UploadWorker.cs) — 当前缓存优先、重试、dead-letter 与调度入口
