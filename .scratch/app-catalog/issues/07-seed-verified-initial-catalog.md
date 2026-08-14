# 07 — Seed the first verified product Catalog

**What to build:** Replace the empty bootstrap Catalog with a reviewed first product snapshot and prove it repairs the known Windows/macOS splits without classifying ambiguous identities. This is a data-and-acceptance slice over the Reconciler, not a heuristic discovery system.

**Blocked by:** 03 — Persist and reconcile the local Override lifecycle.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] Increase catalogVersion and add only identities verified from repository platform adapters, real observed data or supplier documentation; capture non-obvious verification in code comments, test names or issue comments rather than adding provenance fields to runtime JSON.
- [ ] Include Google Chrome: `win:chrome` and `mac:com.google.chrome` → Key `chrome`, DisplayName `Google Chrome`.
- [ ] Include Visual Studio Code: `win:code` and `mac:com.microsoft.vscode` → Key `vscode`, DisplayName `Visual Studio Code`.
- [ ] Include QQ: `win:qq` and `mac:com.tencent.qq` → Key `qq`, DisplayName `QQ`.
- [ ] Include Feishu only after confirming `win:feishu` and `mac:com.electron.lark` are the same product; choose and test one canonical vendor DisplayName.
- [ ] Include Finder as the single-platform product `mac:com.apple.finder` → Key `finder`, DisplayName `Finder`.
- [ ] Verify current and legacy Heartbeat Windows/macOS executable identities from source and observed data, then map only confirmed identities into one `heartbeat` product.
- [ ] Leave `mac:com.openai.codex`, `mac:com.electron.lark.iron` and every other unresolved identity provisional; no display-name inference is introduced.
- [ ] Reconciliation preserves the established formal App Id where possible, applies Catalog Key/DisplayName, and marks matching local Overrides promoted without losing audit history.
- [ ] Integration fixtures seed split Windows/macOS facts for every cross-platform entry and prove Report, Timeline/segments, app details, current activity, icons and App-key knowledge resolve one product while raw identities remain distinct.
- [ ] Product variants are represented separately when present; no Beta/Insiders identity is folded into the stable product without explicit evidence.
- [ ] The production JSON passes canonical ordering/hash validation and a snapshot test guards accidental identity removal or version reuse.
- [ ] Against the local stack dataset, the red Chrome assertion becomes green: the two exact Chrome identities have one distinct AppId after startup reconciliation.

## Primary Seams

- `server/Heartbeat.Server/AppCatalog/app-catalog.json`.
- Reconciler PostgreSQL integration fixtures.
- Existing local-stack SQL assertion documented in the PRD diagnosis.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../docs/adr/034-app-as-cross-platform-product.md`](../../../docs/adr/034-app-as-cross-platform-product.md)
- [`../../../docs/adr/038-server-maintained-app-catalog.md`](../../../docs/adr/038-server-maintained-app-catalog.md)
