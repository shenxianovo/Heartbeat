# 02 — Reconcile built-in Catalog mappings transactionally

**What to build:** Add the App Catalog Reconciler that applies built-in product definitions to existing and future AppIdentity facts. It must generalize the existing merge transaction so a Catalog snapshot can canonicalize product metadata, repair historical provisional splits, and leave every App consumer consistent.

**Blocked by:** 01 — Establish the Catalog artifact and applied-state contract.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Startup reconciliation acquires a deterministic PostgreSQL advisory transaction lock before inspecting or mutating Catalog state; concurrent backend instances serialize and converge.
- [ ] For every built-in product, the Reconciler resolves all declared AppIdentity keys to one App with the Catalog Key and DisplayName.
- [ ] `AppIdentityService.ResolveAsync` (and every equivalent presence/icon resolution path) consults the same effective Catalog snapshot, so a known identity first observed after startup is created directly under the canonical App rather than briefly creating a provisional product.
- [ ] When identities currently belong to multiple Apps, reconciliation preserves an established non-provisional App Id when possible and transactionally removes obsolete provisional products.
- [ ] When no target exists, reconciliation creates the canonical App and binds existing identities. Catalog identities that have never been observed are not pre-created as AppIdentity rows; their first observation binds through the effective snapshot.
- [ ] Reconciliation can change a target App Key/DisplayName while migrating all authoritative App-key knowledge that the current `AppMergeService` protects.
- [ ] ActivitySegment.AppIdentityId remains the observed fact. Legacy ActivitySegment.AppId compatibility rows, Device current activity, AppIcon, Strand Matcher, Muted Matcher, Recurrence Probe, question cache and merge receipt behavior remain consistent.
- [ ] Raw Windows/macOS AppIdentity keys remain queryable after product aggregation.
- [ ] Re-applying the same Catalog is idempotent: no duplicate Apps, identities, audit rows, receipts, icon mutation or knowledge rewrites.
- [ ] A Catalog reconciliation failure rolls back every product, identity, knowledge, icon, cache and state mutation and prevents backend startup.
- [ ] A built-in mapping produces an append-only audit summary with catalogVersion/hash and affected product/identity counts.
- [ ] Existing admin merge API behavior and `AppMergeServiceTests` remain green; shared transaction logic is extracted rather than independently reimplemented.
- [ ] PostgreSQL tests prove two provisional products become one product across Report, segment/detail filtering, current activity, icons and knowledge while raw identities remain distinct.
- [ ] A PostgreSQL test starts with only the canonical product, ingests a previously unseen Catalog identity, and proves no provisional App is created.
- [ ] A concurrency test holds the advisory lock and proves two Reconciler instances do not apply the same snapshot concurrently.

## Primary Seams

- `server/Heartbeat.Server/Services/AppMergeService.cs` — existing transaction and knowledge migration behavior.
- New `AppCatalogReconciler` service — effective built-in snapshot application.
- `server/Heartbeat.Server/Program.cs` — startup ordering and failure boundary.
- `server/Heartbeat.Server.Tests/Services/AppMergeServiceTests.cs` and new Reconciler integration tests.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../docs/adr/034-app-as-cross-platform-product.md`](../../../docs/adr/034-app-as-cross-platform-product.md)
- [`../../../docs/adr/038-server-maintained-app-catalog.md`](../../../docs/adr/038-server-maintained-app-catalog.md)
