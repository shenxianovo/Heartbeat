# Heartbeat Feature Map

This map is an Agent navigation index, not an alternate product specification. Read the linked durable documentation for semantics.

## Local development environment

- Purpose: start and inspect Web, API, PostgreSQL, Hub, and the host macOS Collector.
- User entry: `./scripts/heartbeat-dev env up [web|api|db|hub|desktop]`.
- Agent path: `env status --json`, targeted `env logs`, and `env down`; `env reset` is a preview unless `--apply` is explicit.
- Authority: [development.md](../../../../docs/development.md).
- Pitfall: `desktop` is a foreground host process and currently requires macOS plus `.env.local` from `scripts/setup.sh`.

## Replay experience

- Purpose: authenticate, select Tracks, choose a time window, and inspect stored Record intervals.
- User entry: Web at `/`, OIDC callback at `/auth/callback`; the replay UI is reached after login.
- Agent path: `./scripts/heartbeat-dev scenario replay-fixture`.
- Renderer path: `src/Frontend/Heartbeat.Web/src/components/records/renderers/registry.ts`; Point density curve is drawn by `src/components/replay/DensityCurve.tsx` from day counts and cached detail tiles, and current-range lane visibility is projected by `src/components/replay/timelineProjection.ts`.
- Evidence: deterministic Chromium screenshots, Playwright JSON, and a per-run Web source workspace under the scenario run directory. Concurrent runs share installed dependencies but isolate Next output and generated TypeScript files.
- Authority: [Frontend README](../../../../src/Frontend/Heartbeat.Web/README.md), [replay API](../../../../docs/recording-api.md), and [visualization validation](../../../../docs/validation/experience-visualization.md).
- Pitfall: authentication and API calls are mocked in this scenario; never describe it as real Collector-to-Web E2E.

## Record delivery and replay API

- Purpose: accept Record batches, persist them in PostgreSQL, and query overlapping intervals by Track.
- User entry: HTTP endpoints documented in the API contract.
- Agent path: `./scripts/heartbeat-dev scenario delivery`.
- Evidence: TRX plus command log under the scenario run directory.
- Authority: [recording-api.md](../../../../docs/recording-api.md) and [recording-storage-model.md](../../../../docs/recording-storage-model.md).
- Pitfall: the scenario exercises API/database integration, not the Hub or browser.

## Hub custody and native macOS collection

- Purpose: let the host Collector submit observations to a local Hub that durably assumes custody before backend upload.
- User entry: `./scripts/heartbeat-dev env up desktop` for daily development.
- Agent path: `./scripts/heartbeat-dev scenario native-desktop` for a guided, isolated verification run.
- Evidence: timing, process exit, and Hub queue metadata by default; raw Collector logs only with explicit sensitive-evidence opt-in.
- Authority: [hub-record-delivery.md](../../../../docs/hub-record-delivery.md), [macOS Collector README](../../../../src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md), and [system acceptance](../../../../docs/validation/system-acceptance.md).
- Pitfall: macOS permissions, physical input, lock, and sleep require a human. Do not automate destructive or privacy-sensitive actions.
