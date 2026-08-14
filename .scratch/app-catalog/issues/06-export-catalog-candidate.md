# 06 — Export a deterministic Catalog candidate from Settings

**What to build:** Add the administrator workflow that selects promotable local Overrides and downloads a complete next-version Catalog candidate suitable for code review. The output is a clean artifact, never a database dump and never an import path.

**Blocked by:** 05 — Build the administrator App Catalog Settings UI.

**Status:** ready-for-agent

## Acceptance Criteria

- [ ] The App Catalog settings page lets the administrator explicitly select active local Overrides for promotion; deployment-private mappings are unselected by default.
- [ ] A typed admin export endpoint merges selected mappings with the current built-in snapshot and returns a complete Catalog JSON document.
- [ ] Export contains only schemaVersion, proposed catalogVersion, canonical product Key, DisplayName and identity lists.
- [ ] Export excludes App/AppIdentity database Ids, Owner identifiers, usage counts, administrator sub, timestamps, audit payloads and icons.
- [ ] Proposed catalogVersion equals current formal catalogVersion + 1. Repeated exports before deployment keep the same proposed version even when more selected mappings are added.
- [ ] Product entries sort by Key and identity lists sort canonically; repeated equivalent requests produce byte-identical output and hash.
- [ ] A selection that yields no content difference returns a typed no-change result and does not download a file.
- [ ] The browser downloads `app-catalog.v{version}.candidate.json` using the server-provided bytes without reparsing/reserializing them.
- [ ] Export does not mutate applied Catalog state, Overrides, audit or product mappings.
- [ ] No import/upload control or endpoint is introduced.
- [ ] Server tests assert exact bytes, privacy exclusions, authorization, no-change behavior, version semantics and non-mutation.
- [ ] Frontend tests cover selection, default privacy posture, download filename/content, no-change feedback and errors.

## Primary Seams

- Catalog canonical serializer from ticket 01.
- Admin API/controller and shared DTOs from ticket 04.
- App Catalog settings view from ticket 05.

## References

- [`../PRD.md`](../PRD.md)
- [`../../../docs/adr/038-server-maintained-app-catalog.md`](../../../docs/adr/038-server-maintained-app-catalog.md)
