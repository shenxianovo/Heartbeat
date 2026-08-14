# 01 — Establish the Catalog artifact and applied-state contract

**What to build:** Introduce the versioned built-in App Catalog artifact, strict loader/validator, deterministic content hash, and database records that say which snapshot was successfully applied. This first slice applies an empty Catalog without changing App mappings, proving the artifact and startup state machine before reconciliation logic is added.

**Blocked by:** None.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] `server/Heartbeat.Server/AppCatalog/app-catalog.json` exists as a copied/published server asset with `schemaVersion`, `catalogVersion`, and a products array; the initial snapshot may contain no products.
- [ ] Strongly typed parsing rejects unknown schema versions, duplicate App Keys, duplicate identities across products, non-normalized/invalid identity keys, blank canonical fields, products with no identities, and unstable ordering.
- [ ] Canonical serialization produces the same bytes and SHA-256 hash regardless of input property/list ordering; tests assert exact deterministic output.
- [ ] `schemaVersion` describes the document shape and `catalogVersion` describes content. Changing content without increasing catalogVersion is detected as drift.
- [ ] PostgreSQL stores a singleton applied Catalog state containing schema version, catalog version, content hash, applied timestamp, and enough status to distinguish normal from rollback-compatibility startup.
- [ ] PostgreSQL stores append-only Catalog audit records suitable for later reconciliation/Override events; the migration is represented in `AppDbContext` and its model snapshot.
- [ ] Startup validates the artifact before serving requests and records a successfully applied empty snapshot without touching Apps or AppIdentities.
- [ ] If the database records the same catalogVersion with a different hash, startup fails with an actionable error.
- [ ] If the database records a higher catalogVersion than the binary, startup succeeds in rollback compatibility mode, retains the database state, skips downgrade application, and emits an observable warning.
- [ ] Unit tests cover valid/invalid documents, normalization, canonical ordering/hash, version drift, first application, idempotent repeat, and rollback compatibility.
- [ ] PostgreSQL integration tests cover the migration and applied-state transaction.

## Primary Seams

- `server/Heartbeat.Server/AppCatalog/` — artifact, DTO/parser, validator and canonical serializer.
- `server/Heartbeat.Server/Program.cs` — startup application before request serving.
- `server/Heartbeat.Server/Entities/` and `Data/AppDbContext.cs` — applied state and audit persistence.
- `server/Heartbeat.Server.Tests` — pure Catalog contract tests plus PostgreSQL applied-state tests.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../docs/adr/038-server-maintained-app-catalog.md`](../../../docs/adr/038-server-maintained-app-catalog.md)
