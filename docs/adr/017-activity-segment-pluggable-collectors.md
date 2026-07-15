# ADR-017: ActivitySegment — Pluggable Collectors behind a Local Ingest Hub

## Status: Accepted

## Date: 2026-07-02

(Implemented: loopback ingest hub + browser collector landed; ADR-020/021/022 build on this design. The vscode collector remains planned.)

## Context

### The vision: replay anything, feed it to an LLM

The owner's goal is to be able to **"replay" any moment** of PC activity — and eventually hand the timeline to an LLM for analysis. The current collection agent can only observe **system-level** signals: foreground window, window title, input events, power state. Everything inside an application is invisible from outside.

ADR-015 and ADR-016 are the proof of that limit: an increasingly elaborate machinery (title splitting, click gating, display-layer formatters) built to **infer** in-app activity from the only two signals available externally — title strings and input timing. The inference is lossy by construction:

- The agent guesses "tab switched" from *click near title change*; a browser extension **knows** — `tabs.onActivated` is the semantic event itself, not an inference.
- ADR-015 rejected URL capture because reading URLs *from outside* the browser is unreliable. Inside an extension, it is trivial — the rejection reason voids.
- Review of the click-gating implementation surfaced real defects (title-switch attribution under last-title-wins; no observability on the 1s gate window). For plugin-covered apps these problems **dissolve** rather than get fixed.

### The structural insight: two timelines, each side only knows one

- **Foreground-ness is known only to the agent.** A browser extension knows which tab is active but not whether the browser window is currently foreground (the user may be in VSCode with the tab still "active").
- **In-app state is known only to the app.** The agent sees `msedge` in front and can only parse the title string to guess what's inside.

The truth a replay needs is the **intersection of both timelines**. Neither side alone is complete. So the answer is not a smarter agent — it is **per-app collectors** (browser extension, VSCode plugin, even a Minecraft mod) contributing their own timeline, with the existing agent's collection becoming just one collector among many: the **system collector**, the only one that observes foreground-ness.

This is also where ADR-015's deferred "extraction rule layer / per-app specialization" turns out to live: not in the agent, but **inside each app**.

## Decision

### 1. Topology — the Agent becomes a local ingest hub

Collectors do not talk to the server. The Agent opens a **local ingest interface** (localhost HTTP; loopback only) and per-app collectors push to it. Rationale:

- Reuses the existing machinery exactly once: offline cache & retry (ADR-008), single ApiKey (ADR-004), single upload pipeline, single device identity (`X-Hardware-Id`).
- Collectors stay thin: observe → fold → POST localhost. A browser extension holds no credentials, no retry logic, no server URL.
- **Trust model (known, accepted):** any local process can post to the loopback endpoint. For single-user self-hosted deployment this is accepted — same privacy/threat posture as raw input capture (ADR-012). Not acceptable for multi-tenant machines; revisit then (e.g. shared-secret handshake).

### 2. Data model — `AppUsage` generalizes to `ActivitySegment` (one table)

The system collector's output is not a special table; it is one source writing the common shape. Single-table evolution, existing rows become `source = 'system'`:

```
ActivitySegment {
  Id          uuid      // UUIDv7, collector-generated; idempotency key (InputEvent precedent, ON CONFLICT DO NOTHING)
  DeviceId    FK
  Source      text      // 'system' | 'browser' | 'vscode' | 'minecraft' | ...
  IdentityKey text      // continuation predicate, computed by the collector
  AppId       FK?       // nullable; required for source='system'
  Title       text?     // display label; system = window title, plugins may fill (e.g. page title)
  StartTime   timestamptz
  EndTime     timestamptz
  Attributes  jsonb?    // free-form per source: {url, domain} / {file, project} / {boss, dimension}
}
```

**`Source` vs `AppId` — observer vs subject.** `AppId` says *which app the segment is about*; `Source` says *who observed it*. At one instant two overlapping segments coexist legitimately: `(system, AppId=msedge, IdentityKey=title)` — the agent saying "msedge is foreground" — and `(browser, AppId=msedge, IdentityKey=url)` — the extension saying "this tab is active". Same AppId, different facts. `Source` is what keeps them apart, and it carries three jobs `AppId` cannot: the stats filter (§4), continuation isolation (§3), and frontend track/renderer dispatch.

**`App` stays a first-class entity.** It anchors icons (`AppIcon`) and ranking aggregation and cannot be dissolved into JSONB. Plugin sources may set `AppId` as an *association hint* ("this browser segment happened inside msedge") so replay can nest their track under the app's track and reuse its icon.

### 3. Ingest protocol — closed intervals only; collectors fold their own events

Ontologically everything is an event; a segment is the product of **folding** events through a state machine (`AppMonitorService` is exactly this: foreground/title/power events in, `[start, end]` segments out). The protocol decision is *where the folding obligation lives*:

- **Collectors fold. The hub ingests only closed `[StartTime, EndTime]` intervals.** Folding is the hardest logic in the system — the ADR-014 away state machine and the ADR-016 gating interleave are the evidence — and each collector is the party that best understands its own event semantics. Pushing raw events to the hub would force the least-informed party to implement N per-source state machines plus dangling-segment repair (missing `end`, collector crash, timeout heuristics).
- **Point events are zero-length segments** (`StartTime == EndTime`): a boss kill, a git commit. One shape carries both kinds; no second table, no second upload channel.
- Loss bound: a lost upload loses one bounded interval; UUIDv7 idempotent re-upload (InputEvent precedent) covers retries.

**In-progress segments** reuse the solved pattern: a collector periodically closes-and-reopens its current segment (as the agent already does at flush), and the server glues them back (§3a). A 3-hour VSCode file session is visible as it grows, without any "open segment" protocol state.

#### 3a. Continuation — `CanMerge` generalizes to (Source, IdentityKey)

Server-side cross-batch continuation (ADR-001) currently glues on **same App + same Title + adjacency**. The predicate generalizes to:

> same `Source` + same `IdentityKey` (ordinal) + `currStart ≤ prevEnd + tolerance`

`IdentityKey` is computed by the collector — it is the collector's declaration of "what makes two of my segments the same activity": browser → URL; VSCode → file path; system → normalized App+Title. The current predicate becomes the system source's special case; `UsageMerger.CanMerge` remains the **single predicate source** for client and server. Attributes do not participate in continuation (a volatile attribute must not break gluing); on merge the surviving segment keeps the latest attributes.

### 4. Stats boundary — statistics read only `source='system'`; plugins feed replay

This is not a taste rule; it is forced by **mutual exclusivity**:

- `system` segments are mutually exclusive — exactly one foreground window at a time — so their durations **sum to wall-clock time**. Rankings and reports are meaningful.
- Plugin segments **overlap** system segments by nature (a tab segment ⊆ the msedge foreground segment). Adding them into totals double-counts msedge's time.

There is exactly one mutually-exclusive track — foreground-ness is unique — so: **stats queries filter `Source = 'system'` and are otherwise unchanged.** Replay renders multi-track overlay (one track per source, plugin tracks nested under their `AppId`'s track). Entry flow: stats page → click an app → that app's replay with its plugin tracks. Intersection semantics ("time *really* looking at page X" = tab segment ∩ foreground segment) are deliberately **not** computed server-side in v1 — overlay display first, intersection statistics only when a real need drives the semantics (same methodology as ADR-015's 先脏后治).

### 5. Deliberately deferred

- **No attributes schema registry.** Frontends ship a per-source renderer (the `titleFormatters` per-process registry pattern, already validated); an LLM consumes JSONB as-is. A registry/self-description layer waits until source count makes it hurt.
- **ADR-016 gating stays, demoted to fallback.** For apps with a dedicated collector, title inference is no longer the primary signal. The known gating defects (last-title-wins attribution, gate observability) are deferred — they dissolve for covered apps; revisit for uncovered apps if data warrants.
- **Collector SDK / packaging** (how a Minecraft mod authenticates its Source name, versioning of the ingest API) — after the first real plugin lands.

## Consequences

- ✅ Replay gains semantic, per-app timelines (URL, file, in-game events) that no amount of title parsing could recover — collected where the semantics live.
- ✅ The agent's title machinery (ADR-015/016) is repositioned as the **fallback layer** for apps without a dedicated collector, not the load-bearing path for everything.
- ✅ One table, one upload pipeline, one merge predicate, one idempotency scheme — every new collector reuses all four; marginal cost of a new source is the collector itself plus a frontend renderer.
- ✅ Stats remain exactly as trustworthy as today: the mutually-exclusive system track is untouched by design, enforced by a one-line filter.
- ⚠️ `AppUsage` → `ActivitySegment` is a real migration (rename/generalize + backfill `source='system'` + stats queries gain the filter). One-time, concentrated risk.
- ⚠️ Loopback ingest is unauthenticated — any local process can inject segments. Accepted for single-user self-host; documented; revisit for shared machines.
- ⚠️ Two clocks per overlap (collector's vs agent's) may skew tracks slightly in replay; accepted for display, revisit if intersection stats ever land.
- ⚠️ Plugin coverage is opt-in and partial forever; the replay is only as dense as the collectors installed. The system track guarantees a complete (if coarse) baseline.

## References

- `shared/Heartbeat.Core/ActivitySources.cs` — Source 常量（'system' 保留字）
- `shared/Heartbeat.Core/UsageMerger.cs` — `CanMerge(Source, IdentityKey, adjacency)` + `SystemIdentityKey`
- `shared/Heartbeat.Core/SegmentValidationPolicy.cs` — 插件段完整性校验（允许零长度段）
- `shared/Heartbeat.Core/DTOs/Segments/SegmentUploadRequest.cs` — 插件段上传形状
- `server/Heartbeat.Server/Entities/ActivitySegment.cs` — 泛化实体
- `server/Heartbeat.Server/Migrations/20260702110038_GeneralizeUsageToActivitySegment.cs` — 保数据迁移（backfill source='system'）
- `server/Heartbeat.Server/Services/UsageService.cs` — `SaveSegmentsAsync` 统一摄入例程（幂等 + 续接）
- `server/Heartbeat.Server/Controllers/SegmentController.cs` — `/api/v1/segments`（拒收 source='system'）
- `desktop/Heartbeat.Agent/Workers/SegmentIngestWorker.cs` — loopback ingest 枢纽（`POST /v1/segments`）
- `desktop/Heartbeat.Agent/Services/SegmentIngestService.cs` — 接收缓冲 + 校验
- `desktop/Heartbeat.Agent/Services/SegmentUploadService.cs` — 插件段上传 + 离线缓存
- frontend 回放多轨（pending）
- [ADR-001](./001-server-side-usage-merging.md) — cross-batch continuation this generalizes
- [ADR-008](./008-local-cache-offline-retry.md) — offline cache the hub reuses for all sources
- [ADR-012](./012-input-event-tracking.md) — lossless/raw principle; UUIDv7 idempotency precedent; privacy posture
- [ADR-015](./015-window-title-segment-dimension.md) — title as segment dimension; the inference layer this supersedes for covered apps
- [ADR-016](./016-title-noise-control.md) — click gating, demoted to fallback for uncovered apps
