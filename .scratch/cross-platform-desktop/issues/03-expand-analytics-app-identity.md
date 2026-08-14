# 03 — Expand Analytics to App and AppIdentity products

**What to build:** Redefine App as the cross-platform product users recognize while retaining each platform-observed AppIdentity as immutable evidence. Existing Windows history must be backfilled without losing ActivitySegment identity, and reports must aggregate explicitly related Windows and macOS identities into one product.

**Blocked by:** 01 — Extract the portable Collection Hub and system Collector.

**Status:** ready-for-agent

- [ ] Analytics stores globally unique AppIdentity keys and maps many AppIdentity records to one App with a stable Key and DisplayName.
- [ ] Windows identities normalize to `win:<process-without-.exe>`, macOS identities normalize to `mac:<bundle-id>` with the specified executable fallback, and synthetic identities use `sys:<name>`.
- [ ] Existing Windows App data and ActivitySegments are backfilled to corresponding `win:` AppIdentity records without changing segment Id, IdentityKey, timestamps, Source, title, or attributes.
- [ ] ActivitySegment facts reference AppIdentity, while Report and product-level queries aggregate through AppIdentity to App.
- [ ] Explicitly mapped `win:code` and `mac:com.microsoft.vscode` facts contribute to one `vscode` App report while remaining distinguishable AppIdentity observations.
- [ ] An unknown AppIdentity is accepted by creating a one-to-one provisional App; display-name, process-name, bundle-name, and vendor similarity never trigger an automatic merge.
- [ ] App Keys default to short readable product slugs and require a qualifier only when a real collision must be resolved.
- [ ] PostgreSQL integration tests cover backfill, provisional creation, multi-identity aggregation, raw identity preservation, identity-guard behavior, and Owner/Device isolation.
