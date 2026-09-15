---
name: verify-heartbeat
description: Verify Heartbeat implementation changes with the repository Developer CLI. Use after building, fixing, refactoring, or reviewing Heartbeat code, and whenever evidence is needed for UI, API, Hub, or macOS Collector behavior.
---

# Verify Heartbeat

Use the repository-owned CLI and report what the evidence proves. Do not replace execution with a prose checklist.

1. Record the intended Git comparison base before editing. For ordinary dirty-worktree work, use `HEAD`; for branch work, use the user-provided branch or merge base.
2. Read the relevant entry in [the Feature Map](references/features/README.md) before choosing a scenario. Follow its links to authoritative domain and protocol docs when behavior is involved.
3. Preview the selected checks with `./scripts/heartbeat-dev verify changed --base <ref> --plan`, then run the same command without `--plan`.
4. Run `./scripts/heartbeat-dev quality --base <ref>`. Treat an unavailable analyzer as a failed verification, not a pass.
5. For UI, user-flow, HTTP presentation, performance, or native-runtime changes, run the matching `scenario`. Do not call fixture-backed browser evidence a real end-to-end test.
6. Inspect the run's `manifest.json` and the relevant machine-readable report. In the final response, name the checks, result, evidence path, and limitations.
7. If routes, user entry points, stable selectors, renderers, or scenario commands changed, update the Feature Map in the same change.

The command contract, thresholds, evidence sensitivity rules, and setup commands live in [docs/verification.md](../../../docs/verification.md). Keep this skill short and point there rather than duplicating policy.
