# 02 — Introduce versioned cache and upload failure isolation

**What to build:** Make every Upload Stream durable across format upgrades and capable of making progress when a batch contains permanently invalid data. Temporary failures must remain retryable, rejected records must remain inspectable, and an incompatible server must stop pointless retransmission without silently discarding cached activity.

**Blocked by:** 01 — Extract portable Hub.Core and Desktop.Core.

**Status:** ready-for-agent

- [ ] Durable caches have an explicit format version and are loaded through version-specific persistence DTOs rather than deserializing old files into current domain types.
- [ ] Cache migration runs before normal loading, creates a recoverable backup, writes the replacement atomically, validates it, and only then archives the old representation.
- [ ] A failed migration leaves the original cache recoverable, prevents unsafe normal draining, and exposes an actionable failure state to the desktop presentation layer.
- [ ] Network failures, recoverable authentication failures, HTTP 408, HTTP 429, and HTTP 5xx responses retain their batches and retry without violating the Upload Stream “batch does not evaporate” invariant.
- [ ] HTTP 400 and 422 batches are split until invalid records can be isolated while valid records continue uploading.
- [ ] Permanently rejected records are written as durable, inspectable JSON dead letters with enough response context to diagnose the rejection.
- [ ] HTTP 426 pauses the affected Upload Stream, retains its queued data, and exposes an update-required state instead of retrying indefinitely.
- [ ] Restart recovery tests use real temporary cache files and a controlled HTTP transport to verify migration, backup preservation, compaction, retry classification, batch splitting, dead letters, and paused-stream recovery.
