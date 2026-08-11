# 05 — Cut Windows collection over to the strict AppIdentity protocol

**What to build:** Move the Windows Agent and Analytics ingest boundary to the new AppIdentity contract without retaining a long-lived legacy AppName normalization path. A newly updated Agent must safely convert and replay old segment caches, while old clients receive an understandable update-required response instead of creating ambiguous product facts.

**Blocked by:** 02 — Introduce versioned cache and upload failure isolation; 03 — Expand Analytics to App and AppIdentity products; 04 — Complete App product consumers and admin merge.

**Status:** ready-for-agent

- [ ] The Windows system Collector emits normalized `win:` AppIdentity keys for observed applications and `sys:away` for away periods.
- [ ] New ActivitySegment, Heartbeat presence, Current Activity, and AppIcon requests use AppIdentity fields and no longer rely on AppName as an identity.
- [ ] Analytics accepts the new contract and returns an upgrade-required response for legacy clients or payloads instead of silently normalizing AppName.
- [ ] The Windows Agent pauses affected Upload Streams on an upgrade-required response, retains queued data, and exposes the required-update state to its current UI.
- [ ] Legacy segment caches are upgraded locally before draining: AppName becomes AppIdentityKey, the original IdentityKey and all other segment evidence are preserved, and the migration follows the backup and atomicity guarantees from ticket 02.
- [ ] A migrated legacy cache can replay successfully once against the strict server and cannot become an endlessly rejected poison batch.
- [ ] Windows Current Activity, Dashboard Presence, Report, Timeline, App details, and icon display continue to show the same user-recognizable products after the cutover.
- [ ] Contract and integration tests cover strict rejection, update-required pausing, cache migration, replay idempotency, identity-guard preservation, unknown identities, and Dashboard-facing projections.
