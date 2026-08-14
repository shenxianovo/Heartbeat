# 04 — Expose the typed administrator Catalog API

**What to build:** Turn the reconciliation and Override domain services into a typed, administrator-only HTTP surface. The API supplies the data required by Settings without exposing classification internals to ordinary/public consumers.

**Blocked by:** 03 — Persist and reconcile the local Override lifecycle.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] `MeResponse` includes `IsAdmin`, computed from the existing configured JWT subject whitelist for every authenticated `/me` response.
- [ ] Ordinary authenticated users receive `IsAdmin=false`; missing/renamed username never affects authorization because the check uses JWT sub.
- [ ] Admin endpoints list provisional products, raw AppIdentity keys, canonical product state, effective source (built-in/Override/provisional), active Overrides and relevant per-product usage counts.
- [ ] Admin endpoints expose recent append-only Catalog/Override audit entries without leaking unrelated Owner data.
- [ ] A typed dry-run endpoint previews mapping an identity to an existing product or a new canonical Key/DisplayName, including identities, products removed, icons, current devices, knowledge changes/deduplications and cache invalidations.
- [ ] Typed commit/update/delete endpoints call the domain services from ticket 03 and return stable conflict/not-found/validation errors.
- [ ] Every endpoint independently calls `AdminAuthorizationService`; hiding UI is never treated as authorization.
- [ ] Non-admin requests return 403 and cause no database mutation or audit entry.
- [ ] Public `/users/{username}` DTOs and endpoints do not expose IsProvisional, Override state, raw administrative inventory or audit data.
- [ ] Controller actions return typed `ActionResult<T>`/`Task<T>` so Development OpenAPI contains complete schemas for NSwag.
- [ ] No endpoint imports or replaces the built-in Catalog JSON.
- [ ] Controller/service tests cover admin/non-admin access, list projections, dry-run parity, commit errors, delete behavior and Owner-data privacy.

## Primary Seams

- `server/Heartbeat.Server/Controllers/AdminAppController.cs` — extend or replace the narrow merge surface while preserving compatibility as appropriate.
- `server/Heartbeat.Server/Controllers/MeController.cs` and `shared/Heartbeat.Core/DTOs/Users/MeResponse.cs`.
- New shared Catalog administration DTOs under `shared/Heartbeat.Core/DTOs/Apps/`.
- `server/Heartbeat.Server.Tests/Services/AppMergeServiceTests.cs` and new controller/projection tests.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../docs/api.md`](../../../docs/api.md)
- [`../../../docs/adr/038-server-maintained-app-catalog.md`](../../../docs/adr/038-server-maintained-app-catalog.md)
