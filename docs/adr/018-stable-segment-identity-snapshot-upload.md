# ADR-018: Stable Segment Identity — Snapshot Upload Replaces Heal-on-Ingest Merging

## Status: Accepted

## Date: 2026-07-04

## Context

### Fragments are self-inflicted: the protocol destroys identity at every flush

A segment can be split for three distinct reasons:

1. **Upload truncation (artificial)** — the flush boundary is not an activity boundary. A 2-hour session is sliced into ~120 pieces by a 1-minute upload cycle.
2. **Genuine activity switches (natural)** — the user really changed window/tab/file. Not fragments; facts.
3. **Hub batching (new with ADR-017)** — plugin collectors push every ~30s, the Agent uploads every ~1min, so one server batch carries multiple slices of the same ongoing activity.

Only (1) and (3) need repair, and both exist because of one protocol choice: the Agent generates the segment's Id **at close time** (`AppMonitorService.CloseCurrentSegment` at flush). The fact that two slices were *the same activity* is known perfectly at the collector — and then discarded. The server must reconstruct it heuristically: same `(Source, IdentityKey)` + gap ≤ 1s (`UsageMerger.CanMerge`, ADR-001 generalized by ADR-017 §3a).

Three repair layers exist today: client in-batch merge (`UsageMerger.Merge`, system path only), server first-of-batch continuation (`SaveSegmentsAsync`), and UUIDv7 idempotency dedup. Review of the ADR-017 implementation found the reconstruction incomplete — the server glues only the batch's **first** segment, so the plugin path (no client-side merge layer, hub batches multiple flushes) fragments structurally: one row per flush instead of one row per activity.

### Alternatives considered

- **Complete the heal-on-ingest merging** — group by `(Source, IdentityKey)`, merge chains in-batch, continue each group against the DB. Works, but remains "destroy identity → reconstruct by heuristic"; every future consumer must trust the reconstruction, and the tolerance stays a magic number (ADR-001's own listed ⚠️).
- **Explicit truncation signal** — a `truncated` flag or `continuationOf` pointer on the slice. More honest than a heuristic, but the server still pairs slices: order-sensitive, retry-tricky (flagged slice retransmitted after its successor already merged), in-batch multi-chain handling. Nearly all the complexity survives; only the 1s tolerance is replaced by a flag.
- **Read-time merging** — store fragments, coalesce at query time. Fragmentation never hurts statistics (duration sums are invariant under splitting) — only replay display and row counts. But it pushes coalescing into every consumer (replay, reports, LLM export) and window-boundary gluing is ugly.
- **Raw event upload, server folds** — already rejected by ADR-017 §3; unchanged.

## Decision

**Preserve identity instead of reconstructing it: same activity ⇒ same Id across flushes; the server upserts by primary key.** Truncation ceases to exist in the protocol — no signal needed, because nothing is lost.

### 1. Collector contract — Id at activity start, snapshots at flush

- The collector generates the segment's UUIDv7 **when the activity starts**, not when it closes.
- At each flush the collector uploads a **snapshot** of the ongoing segment: `(Id, StartTime, EndTime = now, attributes so far)` — and keeps the segment open locally under the same Id. The activity's final flush is simply its last snapshot; there is no "end" message.
- A snapshot is a closed interval that **monotonically grows**: `StartTime` fixed, `EndTime` only increases, attributes last-write-wins. This makes ingestion commutative and re-entrant — out-of-order retries, offline caches holding five snapshots of one Id, hub batches mixing snapshots: all converge to the same row.
- `Id` is promoted from *idempotency key* (ADR-017) to *activity identity*. Point events (zero-length) are unaffected — one event, one Id. (Bonus: two boss-kills 1s apart can no longer be glued into one by the tolerance heuristic.)

### 2. Server contract — upsert by PK with an identity guard

`SaveSegmentsAsync` becomes: validate → for each item, if `Id` exists then extend (`EndTime = max`, `StartTime = min` defensively, recompute `DurationSeconds`, attributes last-write-wins), else insert.

- **Identity guard:** an update is applied only if the existing row's `(Source, IdentityKey)` matches the incoming item; on mismatch the item is rejected and logged. A misbehaving collector reusing Ids cannot corrupt foreign rows — the loopback trust model (ADR-017 §1) is unchanged.
- **Cross-batch continuation (`CanMerge`) and the DB latest-record lookup are deleted.** Continuation and idempotency collapse into one mechanism: the primary key. The 1s tolerance and the first-of-batch limitation disappear with them.
- Id-less legacy uploads (pre-UUIDv7 agents) are still accepted — the server generates an Id per item — but are no longer glued. Accepted: single-user self-hosted fleet with auto-update (ADR-009); agents upgrade in lockstep with the server.

### 3. Simplifications that fall out

- **Client in-batch merge retires.** Slices of one activity share an Id, so batch compaction is "keep latest snapshot per Id" (`SnapshotCompaction`) — trivial, and identical for system and plugin paths. `UsageMerger` is deleted; its `SystemIdentityKey` survives as `SystemIdentity.Key` (IdentityKey is still the system source's activity predicate and the query/replay grouping dimension).
- **Hub buffer becomes a dictionary keyed by Id** (later snapshot replaces earlier). Batching compresses automatically; upload volume shrinks.
- **The stored `DurationSeconds` column drops.** Duration is a derived quantity of a growing snapshot; persisting it was an invariant the upsert had to keep repairing. List projections compute `EndTime - StartTime` on the fly; reports clip-and-sum in SQL (§4).
- The composite index `(DeviceId, Source, IdentityKey, EndTime)` is no longer needed for ingest (PK lookup suffices); it is kept for replay/query filtering only.

### 4. Query semantics — interval overlap

Time-window queries switch from `StartTime ∈ [start, end)` to overlap semantics: `EndTime > start AND StartTime < end`. With gluing gone and rows now spanning full activities, a 3-hour segment must be visible in every window it crosses, not only the one containing its start.

## Consequences

- ✅ Fragmentation is structurally impossible, not heuristically repaired — the ADR-017 plugin-path defect (first-of-batch-only gluing) cannot recur.
- ✅ Ingestion is commutative and idempotent by construction; offline retry (ADR-008) needs no ordering assumptions.
- ✅ Net code deletion: two merge layers, the continuation query, and the tolerance magic number are removed; collectors get a simpler mental model ("report what Id X has done so far").
- ✅ In-progress activities are visible server-side as they grow (each snapshot extends the row) — the ADR-017 §3 goal, achieved without dangling-segment repair.
- ⚠️ One UPDATE per flush per ongoing activity (vs INSERT-only today). Same order of magnitude; total row count drops sharply.
- ⚠️ Agent restart mid-activity starts a new Id → a split at the restart point. Accepted: an observation boundary is a truthful boundary.
- ⚠️ Old agents (Id-less uploads) fragment again until upgraded. Accepted for a self-updating single-user fleet.
- ⚠️ `Id` misuse by a collector now has row-level blast radius; contained by the identity guard to the collector's own rows.

## References

- Supersedes [ADR-001](./001-server-side-usage-merging.md) — dual-layer heal-on-ingest merging retired
- Amends [ADR-017](./017-activity-segment-pluggable-collectors.md) §3/§3a — "collectors close-and-reopen, server glues" becomes "collectors snapshot, server upserts"; §3's closed-intervals-only principle is preserved (every snapshot is a closed interval; no dangling-segment protocol state)
- [ADR-008](./008-local-cache-offline-retry.md) — offline cache whose retry becomes order-insensitive
- [ADR-009](./009-velopack-auto-update.md) — auto-update, basis for dropping legacy gluing
- `desktop/Heartbeat.Agent/Services/AppMonitorService.cs` — Id generation moves to activity start; flush emits snapshots
- `desktop/Heartbeat.Agent/Services/SegmentIngestService.cs` — hub buffer keyed by Id
- `shared/Heartbeat.Core/SystemIdentity.cs` — system IdentityKey definition (sole survivor of the retired `UsageMerger`)
- `shared/Heartbeat.Core/SnapshotCompaction.cs` — keep-latest-per-Id batch compaction
- `server/Heartbeat.Server/Services/UsageService.cs` — `SaveSegmentsAsync` upsert with identity guard
