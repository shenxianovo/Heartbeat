# 07 — Ship cross-platform physical-key statistics on Windows

**What to build:** Establish the versioned physical-key contract using Windows as the first producer, while preserving the truthful meaning of historical Windows virtual-key data. Existing and new events must contribute to one Keyboard Heatmap without rewriting stored raw codes or conflating local interaction gating with persisted InputEvent Recording.

**Blocked by:** 01 — Extract portable Hub.Core and Desktop.Core; 02 — Introduce versioned cache and upload failure isolation; 05 — Cut Windows collection over to the strict AppIdentity protocol.

**Status:** ready-for-agent

- [ ] InputEvent carries an explicit CodeSet, with new physical-position events using `heartbeat-key-position-v1` and historical Windows events using `windows-vk-v1`.
- [ ] The Windows input adapter maps native key-down observations to stable physical positions such as `KeyA`, `Digit1`, and `MetaLeft`, independent of produced characters or active keyboard layout.
- [ ] Existing stored input rows and legacy input caches are tagged `windows-vk-v1` without guessing or rewriting their raw Code values.
- [ ] Legacy input-cache migration preserves event Id, timestamp, device identity, event type, and Code while following the backup and atomicity guarantees from ticket 02.
- [ ] Analytics and Dashboard projections understand both CodeSets so an equivalent historical VK event and new physical-position event contribute to the same displayed key.
- [ ] Raw event storage remains truthful and queryable; projection compatibility does not mutate historical records.
- [ ] Interaction Signal remains local and ephemeral, InputEvent Recording remains independently configurable and durable, and disabling recording prevents persistence/upload without disabling title gating.
- [ ] Existing mouse button and wheel semantics, UUIDv7 idempotency, long-press filtering, and Keyboard Heatmap privacy boundaries remain intact.
- [ ] Tests cover Windows native mappings, layout independence, both CodeSets, cache migration, retry idempotency, projection equivalence, and separate Interaction Signal/InputEvent settings.
