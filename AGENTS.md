# Agent Guidelines

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
