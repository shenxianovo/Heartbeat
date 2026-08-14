# 03 — Persist and reconcile the local Override lifecycle

**What to build:** Add explicit App Catalog Override persistence and domain operations. An Override records deployment-administrator intent separately from the effective AppIdentity.AppId result, wins over the built-in Catalog, can target an existing or newly created App, and has reversible deletion/promotion semantics.

**Blocked by:** 02 — Reconcile built-in Catalog mappings transactionally.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] PostgreSQL persists one active Override per AppIdentity, its target App, lifecycle status, creating/updating administrator sub, and timestamps; foreign keys and uniqueness constraints prevent dangling or competing active intent.
- [ ] Override mutations append immutable audit records. Updating an Override preserves previous history rather than overwriting the only evidence.
- [ ] The Reconciler computes effective mapping in the order: active local Override → built-in Catalog → independent provisional App.
- [ ] Creating an Override to an existing App uses the same dry-run/commit impact model as product reconciliation and migrates every protected consumer transactionally.
- [ ] Creating an Override for a new product creates canonical App Key/DisplayName and binds the identity in the same transaction.
- [ ] App Key collisions, invalid keys, same-target no-ops and attempts to target an obsolete/deleted product return stable domain errors without mutation.
- [ ] Deleting an Override immediately reconciles the identity to the built-in Catalog when declared there.
- [ ] Deleting an Override with no built-in mapping splits that identity into an independent provisional App; historical segments follow via AppIdentity while legacy AppId rows and other protected consumers remain consistent.
- [ ] When a later built-in Catalog contains the same mapping, the Override becomes promoted/inactive, stops shadowing future Catalog corrections and retains its full audit trail.
- [ ] An active Override shadowing a different built-in mapping causes a warning/audit entry but does not fail startup or mutate the Override target.
- [ ] Concurrent create/update/delete requests serialize and remain idempotent.
- [ ] PostgreSQL tests cover precedence, existing/new target, update, both delete branches, promotion, collision, rollback, concurrency and audit retention.

## Primary Seams

- New `AppCatalogOverride` entity and EF migration/configuration.
- App Catalog Reconciler effective-source calculation.
- Existing App merge/reconciliation transaction seam.
- PostgreSQL integration tests in `server/Heartbeat.Server.Tests`.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../server/CONTEXT.md`](../../../server/CONTEXT.md)
- [`../../../docs/adr/038-server-maintained-app-catalog.md`](../../../docs/adr/038-server-maintained-app-catalog.md)
