# Agent Guidelines

## Rewrite constraints

Until the rewrite is complete, this project will not be deployed. All rewrite changes are reversible; existing data, clients, and interfaces carry no compatibility requirements.

- Change database schemas directly in the Initial migration and update the model snapshot; do not add incremental migrations for rewrite changes.
- Change protocol fields and their producers/consumers directly; do not add or bump protocol versions to preserve earlier rewrite behavior.
- Keep one current implementation, updating its tests and documentation together. Avoid compatibility adapters, migration paths, and version negotiation.
- CI is deferred until the user requests it. Run appropriate checks locally.

## Design decisions

When a task reveals a design or architecture issue, explain it and confirm the proposed decision with the user before implementing it. Continue independent fixes within already agreed rules while that decision is pending.

## Agent skills

### Issue tracker

In-progress issues and specs are tracked as local markdown files under `.scratch/<feature-slug>/`. See `docs/agents/issue-tracker.md`.
`.scratch` is for one-time implementation references only. After a feature is complete, delete the scratch files and move durable knowledge into `CONTEXT.md`, ADRs, `docs/`, or the relevant package README.

### Triage labels

This repo uses the default five canonical triage labels. See `docs/agents/triage-labels.md`.

### Domain docs

This repo uses a single-context domain doc layout. See `docs/agents/domain.md`.

### Verification

For implementation changes, use the repository verification skill at `.agents/skills/verify-heartbeat/SKILL.md`. It selects checks from a Git base, applies structural quality ratchets, and records scenario evidence without overstating mocked coverage.
