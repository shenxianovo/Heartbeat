# 06 — Resolve external Collector App hints

**What to build:** Let an external Collector describe the logical App it is observing without embedding Windows process names or macOS bundle identifiers. The local hub must resolve that hint through its platform adapter so Collector evidence remains associated with the same App product as the system Collector on both operating systems.

**Blocked by:** 05 — Cut Windows collection over to the strict AppIdentity protocol.

**Status:** ready-for-agent

- [ ] The loopback ingest contract offers external Collectors a logical App hint that is independent of Windows and macOS native identity syntax.
- [ ] Platform knowledge for turning a logical hint into an AppIdentity key lives behind the hub platform resolver, not in Collector implementations or shared hub runtime code.
- [ ] The browser Collector uses the logical hint contract and no longer needs to hard-code a Windows process identity.
- [ ] On Windows and macOS, the same browser hint resolves to the platform AppIdentity that maps to the same App as the corresponding system segment.
- [ ] Unknown, missing, or ambiguous hints have deterministic behavior that preserves the Collector segment without inventing a cross-product merge.
- [ ] Collector deactivation, Source identity, ActivitySegment IdentityKey, attributes, and observation-depth declarations remain unchanged by App hint resolution.
- [ ] Replay Label Upgrade and App detail queries associate browser evidence with the correct product for both Windows and macOS AppIdentity values.
- [ ] End-to-end tests cover logical hint ingestion, platform resolution, unknown hints, strict Analytics upload, and browser Replay/App detail behavior.
