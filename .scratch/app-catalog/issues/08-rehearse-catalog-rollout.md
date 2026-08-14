# 08 — Rehearse Catalog rollout and cross-surface acceptance

**What to build:** Exercise the complete feature against the local stack and production-shaped failure modes. Prove that Catalog deployment, local Overrides, Web administration, export, multi-instance startup and rollback behavior agree across Analytics and Dashboard before the feature is considered complete.

**Blocked by:** 06 — Export a deterministic Catalog candidate from Settings; 07 — Seed the first verified product Catalog.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] A fresh database applies the current Catalog once, records version/hash/audit, serves requests, and repeats startup idempotently.
- [ ] An existing database containing split Chrome/VS Code products reconciles to one product each without losing ActivitySegment Id/IdentityKey/timestamps/source/title/attributes or raw AppIdentity keys.
- [ ] The exact Chrome SQL feedback loop reports one distinct AppId for `win:chrome` and `mac:com.google.chrome` after reconciliation.
- [ ] Two backend instances starting concurrently serialize through the advisory lock and produce one applied-state/audit result.
- [ ] Invalid JSON, duplicate identity, reused version with different hash and transactional merge failure each prevent startup without partial mutation.
- [ ] A backend rollback carrying an older Catalog starts in compatibility mode, retains the newer database mapping, skips downgrade and emits an observable warning.
- [ ] Active local Override beats a conflicting built-in entry; deletion exercises both Catalog fallback and provisional split; a later matching Catalog promotes the Override and preserves history.
- [ ] `/me.isAdmin`, every admin API and every Settings route behave correctly for administrator, ordinary authenticated user and anonymous/public caller.
- [ ] The Settings workflow classifies a provisional identity through dry-run and commit, refreshes Report/Timeline/detail views, and preserves raw identity diagnostics.
- [ ] Candidate export includes selected mappings only, excludes runtime/private fields, remains byte-stable and does not change applied version until the new JSON is deployed.
- [ ] Admin-only provisional markers never appear in public/ordinary-user API responses or rendered views.
- [ ] Update `docs/development.md`/`docs/api.md` with the Catalog artifact validation, admin-sub configuration, startup/rollback behavior, candidate export-to-code-review workflow and diagnostic queries. Auth platform remains the documented source of JWT sub.
- [ ] Run server PostgreSQL integration tests, shared Core tests, frontend tests/type-check, compose config validation and local backend/frontend builds.
- [ ] Remove temporary debug instrumentation and preserve the local database unless the operator explicitly requests reset/refresh.

## Verification Baseline

At minimum, the closeout report includes the exact commands and results for:

```text
dotnet test server/Heartbeat.Server.Tests/Heartbeat.Server.Tests.csproj
dotnet test shared/Heartbeat.Core.Tests/Heartbeat.Core.Tests.csproj
cd frontend && npm test && npx vue-tsc -b
docker compose --file compose.local.yml --env-file .env.local config --quiet
docker compose --file compose.yml --env-file .env.example config --quiet
```

Use the repository's actual frontend test script if it differs; do not add a duplicate script merely to match this example.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../docs/development.md`](../../../docs/development.md)
- [`../../../docs/api.md`](../../../docs/api.md)
- [`../../../docs/adr/038-server-maintained-app-catalog.md`](../../../docs/adr/038-server-maintained-app-catalog.md)
