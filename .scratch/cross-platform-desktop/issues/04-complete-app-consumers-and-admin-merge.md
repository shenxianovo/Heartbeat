# 04 — Complete App product consumers and admin merge

**What to build:** Make every product-facing consumer consistently resolve AppIdentity observations to App, and provide a safe administrative operation for correcting provisional or duplicate products. A merge must preview and reconcile all affected product knowledge rather than merely changing one foreign key.

**Blocked by:** 03 — Expand Analytics to App and AppIdentity products.

**Status:** ready-for-agent

- [ ] Presence accepts CurrentAppIdentityKey, stores the current AppIdentity, and projects both the App product identity and raw AppIdentity key where the Dashboard needs them.
- [ ] Report, Replay label association, App detail queries, and other product views consistently aggregate or filter through AppIdentity to App.
- [ ] App-related Matcher knowledge, muted Matchers, Recurrence Probes, Device Current Activity, and other authoritative product references retain their meaning when identities are rebound.
- [ ] AppIcon upload resolves AppIdentity to App and keeps one icon per Owner/App; the first valid icon remains stable unless an explicit refresh occurs.
- [ ] Product DTOs expose stable App Key and DisplayName instead of presenting platform process names or bundle identifiers as the App label.
- [ ] A configured administrator can dry-run a merge and inspect affected AppIdentity mappings, product knowledge, icons, and provisional Apps without changing data.
- [ ] Committing the same merge is transactional and idempotent, reconciles icons and authoritative knowledge, removes obsolete provisional Apps, and invalidates derived question caches while leaving historical Recap prose unchanged.
- [ ] Non-admin callers cannot mutate global AppIdentity mappings, and no ordinary-user mapping UI is introduced.
- [ ] PostgreSQL integration tests cover authorization, dry-run accuracy, commit, rollback, retry idempotency, icon reconciliation, knowledge migration, and product-query continuity.
