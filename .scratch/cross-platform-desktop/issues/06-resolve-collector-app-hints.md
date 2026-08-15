# 06 — Resolve external Collector App hints

**What to build:** Let an external Collector describe the logical App it is observing without embedding Windows process names or macOS bundle identifiers. The local hub must resolve that hint through its platform adapter so Collector evidence remains associated with the same App product as the system Collector on both operating systems.

**Blocked by:** 05 — Cut Windows collection over to the strict AppIdentity protocol.

**Status:** ready-for-human

- [x] The loopback ingest contract offers external Collectors a logical App hint that is independent of Windows and macOS native identity syntax.
- [x] Platform knowledge for turning a logical hint into an AppIdentity key lives behind the hub platform resolver, not in Collector implementations or shared hub runtime code.
- [x] The browser Collector uses the logical hint contract and no longer needs to hard-code a Windows process identity.
- [x] On Windows and macOS, the same browser hint resolves to the platform AppIdentity that maps to the same App as the corresponding system segment.
- [x] Unknown, missing, or ambiguous hints have deterministic behavior that preserves the Collector segment without inventing a cross-product merge.
- [x] Collector deactivation, Source identity, ActivitySegment IdentityKey, attributes, and observation-depth declarations remain unchanged by App hint resolution.
- [x] Replay Label Upgrade and App detail queries associate browser evidence with the correct product for both Windows and macOS AppIdentity values.
- [x] End-to-end tests cover logical hint ingestion, platform resolution, unknown hints, strict Analytics upload, and browser Replay/App detail behavior.

## Comments

### 2026-08-15 — Existing implementation verified on current HEAD

- Implementation landed in `dc44493`. The loopback DTO accepts only platform-neutral `AppHint`; the portable hub owns only resolution result semantics, while Windows and macOS platform heads own their process-name and bundle-identifier mappings.
- Browser brand detection emits logical hints without guessing generic or conflicting Chromium brands. Missing, unknown, and ambiguous hints preserve the original Source, IdentityKey, title, attributes, and segment while leaving AppIdentity unassociated.
- Strict-upload integration proves resolved browser evidence reaches the same App dimension used by Replay and App detail queries without leaking the loopback hint into Analytics.
- Current verification passed all 75 Browser Collector tests and its production build, plus the full 635-test .NET suite containing hub, platform resolver, PostgreSQL strict-ingest, Replay, and App-detail coverage.
