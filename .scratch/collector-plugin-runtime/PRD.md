# Collector Plug-and-Play Runtime

Status: needs-info

## Idea

Future exploration: make Collectors plug-and-play and investigate dynamic dependency injection for discovering, composing, activating, updating, or removing Collector capabilities without rebuilding the desktop host.

The user has a relevant paper and will provide it later. This note records the direction only; it is not an accepted design and is not part of cross-platform desktop issue 12.

## Boundary

- Use the canonical product term **Collector**; “plugin” is only the informal name for this future mechanism.
- Do not choose an in-process or out-of-process architecture yet.
- Do not assume that installation, activation, App coverage, hot reload, unloading, isolation, versioning, or dynamic DI are the same problem.
- Any future design must explicitly reconcile ADR-017's loopback Collector topology and ADR-037's executable/assembly boundaries rather than silently bypassing them.

## Questions intentionally deferred

- What exact problem and mechanism does the paper establish?
- Does “plug-and-play” mean discovery, installation, activation, hot replacement, or all four?
- Which dependencies may be injected dynamically, and which trust or platform boundaries must remain static?
- How does a Collector declare supported Apps, capabilities, permissions, version compatibility, and coverage completeness?
- What are the failure, rollback, isolation, and update semantics?

## Next trigger

When the paper is supplied, attach its citation or local copy, extract its applicable claims, compare them with the existing Collector topology, and start a separate design grill only if requested.
