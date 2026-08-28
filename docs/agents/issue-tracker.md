# Issue tracker: Local Markdown

Issues and PRDs for this repo live as markdown files in `.scratch/`.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`
- The PRD is `.scratch/<feature-slug>/PRD.md`
- Implementation issues are `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01`
- Triage state is recorded as a `Status:` line near the top of each issue file (see `triage-labels.md` for the role strings)
- Comments and conversation history append to the bottom of the file under a `## Comments` heading

## Lifecycle closeout

- `done` means every acceptance checkbox is complete and the verification evidence is recorded. It is a terminal state, not a triage role.
- Code complete with a remaining real-device, account, release, or other human gate is `ready-for-human`, not `done`.
- When implementation makes an issue complete, update its status and checkboxes in the same change. Then reconcile the feature PRD: it is `done` only when every required issue is done; otherwise its status names the remaining gate.
- Before creating a follow-up, search `.scratch/` for an existing issue. Prefer updating the existing source of truth over adding a second backlog entry.

## When a skill says "publish to the issue tracker"

Create a new file under `.scratch/<feature-slug>/` (creating the directory if needed).

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the issue number directly.
