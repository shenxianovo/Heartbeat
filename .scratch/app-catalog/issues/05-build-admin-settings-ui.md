# 05 — Build the administrator App Catalog Settings UI

**What to build:** Add the admin-only Dashboard settings surface for discovering and classifying provisional Apps. The page consumes the typed API, requires an explicit dry-run before commit, and exposes classification state without leaking it to non-admin users.

**Blocked by:** 04 — Expose the typed administrator Catalog API.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Regenerate `frontend/src/api/client.ts` from the Development OpenAPI document after ticket 04 and keep hand-written wrappers in `frontend/src/api/index.ts` narrow and typed.
- [ ] `fetchMe()` exposes `isAdmin`; `/settings` renders an “App Catalog” management row only when it is true.
- [ ] A protected `/settings/app-catalog` route loads provisional products, raw identities, effective source, usage context, active Overrides and recent audit data.
- [ ] Direct navigation by a non-admin never shows stale privileged content: the API returns 403 and the view returns to Settings with a clear message.
- [ ] The provisional list identifies product label, canonical/provisional key, platform identity, recent/aggregate usage context and whether the identity is shadowed by an Override.
- [ ] The administrator can choose an existing target App or define a new canonical Key/DisplayName.
- [ ] Commit is disabled until a successful dry-run for the current form state has returned. Editing any input invalidates the preview.
- [ ] The preview renders affected identities/products, icon resolution, current devices, knowledge changes/deduplications and cache invalidation counts before confirmation.
- [ ] Commit/update/delete actions show pending/success/failure states, refresh server truth after success and never optimistically invent mapping state.
- [ ] Deleting an Override explains whether the identity will fall back to the built-in Catalog or become provisional, based on server preview.
- [ ] Recent audit entries distinguish built-in reconciliation, Override mutation and promoted Override.
- [ ] Administrators see a subtle pending-classification marker for provisional Apps in authenticated Dashboard product views; non-admin/public views receive no classification overlay.
- [ ] Component tests cover admin navigation, 403 handling, preview invalidation, existing/new target flows, delete confirmation, audit display and error recovery.
- [ ] `npx vue-tsc -b` and the frontend test suite pass with the regenerated client.

## Primary Seams

- `frontend/src/views/SettingsView.vue` and a new App Catalog settings view.
- `frontend/src/router/index.ts` — authenticated management route.
- `frontend/src/api/index.ts` and generated `frontend/src/api/client.ts`.
- Existing Dashboard app-name projections plus an admin-only classification overlay.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../docs/development.md`](../../../docs/development.md)
- [`../../../docs/adr/036-shared-cross-surface-visual-language.md`](../../../docs/adr/036-shared-cross-surface-visual-language.md)
