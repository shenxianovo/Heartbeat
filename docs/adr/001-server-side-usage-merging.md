# ADR-001: Server-Side Usage Record Merging

## Status: Superseded by [ADR-018](./018-stable-segment-identity-snapshot-upload.md)

## Date: 2026-02-26

[`328b754`](https://github.com/shenxianovo/heartbeat/commit/328b754) — feat: merge overlapping app usage durations

## Context

The desktop agent uploads usage records in periodic batches (e.g., every 5 minutes). When the agent truncates an ongoing session at upload time, the same app's usage gets split across consecutive batches. This creates fragmented records in the database (e.g., "VSCode 14:00–14:05" and "VSCode 14:05–14:10" instead of a single "VSCode 14:00–14:10").

We considered two approaches:

1. **Client-only merge**: The agent merges fragments before uploading. Simple, but if the agent crashes or restarts between batches, fragments survive in the DB.
2. **Server-side merge**: The server detects overlapping/adjacent records for the same device+app and merges them on write.

## Decision

Adopted **dual-layer merging** — the client merges locally via `UsageMerger` before upload, and the server performs a final merge check against the most recent DB record for the same device+app.

Added a composite index `(DeviceId, AppId, EndTime)` to make the "find latest record" query efficient.

## Consequences

- ✅ Data consistency: even if the client restarts or sends duplicates, the server produces clean, non-overlapping records.
- ✅ The composite index keeps the merge-lookup fast (single index seek per batch).
- ⚠️ Every upload batch triggers an extra DB read (latest record lookup) even when no merge is needed.
- ⚠️ The merge tolerance (1 second) is a magic number — too large risks merging genuinely separate sessions.

## References

- `shared/Heartbeat.Core/UsageMerger.cs`、`server/Heartbeat.Server/Services/UsageService.cs` 与
  `server/Heartbeat.Server/Data/AppDbContext.cs` — `328b754` 时的历史实现路径；旧 merge 与索引形状
  已随本 ADR 被替代，不链接当前同名文件冒充旧实现
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) — 当前稳定 FactId / snapshot upsert 语义
