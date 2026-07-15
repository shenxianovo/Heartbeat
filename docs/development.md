# Development Guide

本地开发与验证的完整流程。项目定位与架构见 [README](../README.md),领域术语见
[CONTEXT-MAP](../CONTEXT-MAP.md) 与各上下文的 `CONTEXT.md`。

## Local End-to-End Verification

Verify local changes end-to-end **before pushing** — spins up Postgres + backend + frontend
from local source (not the published images), so you see *your* changes running against a
clean database. Your real desktop Agent points at this local stack, giving a full loop:
keypress/window-switch → local DB → local dashboard.

Auth uses the real Auth platform (backend validates JWTs against it; the Agent exchanges its
real API key for a session JWT — see [ADR-024](adr/024-oidc-jwt-authentication.md)). Nothing
about auth is stubbed — only Postgres/backend/frontend are local.

**Prerequisites:** Docker Desktop running.

### 1. One-time setup

```powershell
Copy-Item .env.local.example .env.local
# .env.local is gitignored; the defaults already point at the real Auth platform.
```

### 2. Start the local stack

```powershell
docker compose -f compose.local.yml --env-file .env.local up --build
```

- Frontend + API: <http://localhost:8080> (nginx reverse-proxies `/api/` to the backend)
- Schema auto-migrates on startup (ADR-013), so no manual migration step.
- Rebuild after code changes: re-run the same command (`--build` rebuilds changed layers).

### 3. Point the desktop Agent at the local stack

Set the `HEARTBEAT_API_BASE_URL` environment variable before launching the Agent — it
overrides the upload target **for that process only, without touching config.json**
(auth still goes to the real platform via the unchanged `AuthServiceBaseUrl`):

```powershell
$env:HEARTBEAT_API_BASE_URL = "http://localhost:8080"
# then launch the Agent from the same shell:
dotnet run --project desktop/Heartbeat.Agent.Runner
# or run Heartbeat.WPF from this shell
```

Closing the shell reverts everything — no config to restore. Use the keyboard, switch
windows, then open <http://localhost:8080>; data should appear within an upload interval.

### 4. Regenerate the API client (when server DTOs/endpoints changed)

The backend runs in Development here, so it exposes the OpenAPI document at
`/openapi/v1.json` (nginx proxies `/openapi/` to the backend; in production the backend
simply doesn't serve it). Requires the NSwag CLI
(`dotnet tool install --global NSwag.ConsoleCore`):

```powershell
nswag openapi2tsclient /input:http://localhost:8080/openapi/v1.json /output:frontend/src/api/client.ts
```

**Then verify types and rebuild the frontend image:**

```powershell
cd frontend; npx vue-tsc -b; cd ..          # type-check against the regenerated client
docker compose -f compose.local.yml --env-file .env.local up -d --build frontend
```

Client conventions (see `frontend/src/api/index.ts` and [docs/api.md](api.md)):

- Query endpoints return typed responses because their controller actions return
  `ActionResult<T>` (or `Task<T>`) — the OpenAPI schema is inferred from the return type,
  so NSwag generates typed methods. An action typed `IActionResult` produces a schema-less
  `200` and NSwag emits `Promise<void>`; avoid it for anything the frontend reads.
- The `fetchPublic*` wrappers call the generated client methods directly. The **only**
  exceptions are the daily/weekly report wrappers, which hand-build the query string so the
  browser's local timezone offset survives in the `date` parameter (the server's
  `DateRange.Day/Week` needs it to fix "today"/"this week" boundaries — see
  `shared/CONTEXT.md`). `usage`/`segments` use UTC instants, which are unaffected.

### 4b. Feed plugin segments through the local ingest hub (ADR-017)

The Agent opens a loopback ingest hub (`http://127.0.0.1:24820/v1/segments`, `ingestPort`
in config.json) that per-app collectors (browser extension, VSCode plugin, …) POST folded
segments to; the hub forwards them through the same offline-cache + upload pipeline as
system usage. To exercise it without a real collector, POST a segment yourself while the
Agent is pointed at the local stack:

```powershell
$body = @{ segments = @(@{
  source = "browser"; identityKey = "https://example.com/page"; appName = "msedge"
  title = "Example"; startTime = (Get-Date).ToUniversalTime().AddMinutes(-5).ToString("o")
  endTime = (Get-Date).ToUniversalTime().ToString("o")
  attributes = @{ url = "https://example.com/page" }
}) } | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri http://127.0.0.1:24820/v1/segments -Method Post `
  -ContentType application/json -Body $body
```

`source = "system"` is rejected by the hub — that name is reserved for the built-in
collector so plugins can't pollute the mutually-exclusive stats track. The segment shows up
under the app's replay modal (stats page → click an app) within an upload interval.

To run the real browser collector against the local hub, see
[collectors/browser/README.md](../collectors/browser/README.md).

### 5. Tear down

```powershell
docker compose -f compose.local.yml down
```

The database is not persisted (no volume), so every run starts clean.

## Running Tests

Server and shared tests use [Testcontainers](https://dotnet.testcontainers.org/) to spin up
a throwaway Postgres — **Docker Desktop must be running** or every DB-backed test fails
immediately with `DockerUnavailableException`:

```powershell
dotnet test                                        # everything
dotnet test server/Heartbeat.Server.Tests          # server services (needs Docker)
dotnet test desktop/Heartbeat.Agent.Tests          # agent state machine & ingest hub (no Docker)
dotnet test shared/Heartbeat.Core.Tests            # merger / validation / DateRange (no Docker)
```

The browser collector has its own vitest suite:

```powershell
cd collectors/browser; npm test
```
