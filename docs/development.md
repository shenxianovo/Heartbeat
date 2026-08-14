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

macOS/Linux:

```bash
cp .env.local.example .env.local
```

### 2. Start the local stack

```powershell
./scripts/start-local.ps1
```

macOS/Linux:

```bash
./scripts/start-local.sh
```

- Frontend + API: <http://localhost:8080> (nginx reverse-proxies `/api/` to the backend)
- Schema auto-migrates on startup (ADR-013), so no manual migration step.
- The local database is stored under `.local/postgres-data` in the checkout and survives ordinary
  `down/up`. This path works with Docker Desktop on Windows and is ignored by Git.
- Works with an empty database (first run) or one seeded by `refresh-local-data.ps1`.

### 3. Point the desktop Agent at the local stack

Set the `HEARTBEAT_API_BASE_URL` environment variable before launching the Agent — it
overrides the upload target **for that process only, without touching config.json**
(auth still goes to the real platform via the unchanged `AuthServiceBaseUrl`):

```powershell
$env:HEARTBEAT_API_BASE_URL = "http://localhost:8080"
# launch the Windows desktop Agent from the same shell:
dotnet run --project collection/desktop/Heartbeat.Desktop.Windows
```

Closing the shell reverts everything — no config to restore. Use the keyboard, switch
windows, then open <http://localhost:8080>; data should appear within an upload interval.

On macOS, launch the Avalonia menu-bar app from the same shell:

```bash
export HEARTBEAT_API_BASE_URL=http://localhost:8080
dotnet run --project collection/desktop/Heartbeat.Desktop.Mac/Heartbeat.Desktop.Mac.csproj
```

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
  exceptions are date-window endpoints (daily/weekly reports and daily Recap), which
  hand-build the query string so the browser's local timezone offset survives in the `date`
  parameter. NSwag serializes `Date` with `toISOString()` and would otherwise erase that
  offset, while the server's `DateRange.Day/Week` needs it to fix local-day boundaries (see
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
[Browser Collector README](../collection/collectors/Heartbeat.Collector.Browser/README.md).

### 5. Refresh local data from the server (optional)

When a realistic history is needed, replace the local database with a read-only snapshot of the
server database. The script runs `pg_dump` inside the server's Postgres container over SSH, so the
server does not expose port 5432 and the local application never connects to production.

Prerequisites:

- You are authorized to copy all data in that database. The dump includes private activity,
  browser metadata, account identifiers, and generated Recaps.
- The SSH account can run Docker, and the remote directory contains `compose.yml` plus `.env`.
  SSH key and password authentication are both supported.
- The local checkout is at least as new as the deployed server. The script checks EF migration IDs
  before it starts the application.

Run the script without arguments and follow its prompts. The remote directory defaults to
`/srv/heartbeat`, so press Enter to accept it:

```powershell
./scripts/refresh-local-data.ps1
```

macOS/Linux:

```bash
./scripts/refresh-local-data.sh
```

Command-line parameters remain available for repeatable runs:

```powershell
./scripts/refresh-local-data.ps1 `
  -SshDestination user@your-server `
  -RemoteDirectory /srv/heartbeat
```

macOS/Linux:

```bash
./scripts/refresh-local-data.sh \
  --ssh-destination user@your-server \
  --remote-directory /srv/heartbeat
```

`-RemoteDir` is accepted as a shorter alias for `-RemoteDirectory`; the shell equivalent is
`--remote-dir`. For a non-default SSH key or
port, add `-IdentityFile ~/.ssh/id_ed25519` or `-SshPort 2222`. If key authentication is unavailable,
OpenSSH prompts for the account password during step 1; the password is not echoed, stored, or passed
as a command-line argument.
The shell script uses the equivalent `--identity-file` and `--ssh-port` options.
The operation is deliberately one-way and replaces **only** `.local/postgres-data`;
it never writes to the server database. `pg_dump` provides a transaction-consistent snapshot while
the server remains online. The temporary custom-format dump is deleted after restore unless
`-KeepDump` (PowerShell) or `--keep-dump` (shell) is explicitly supplied.

Do not connect a local backend directly to production PostgreSQL: local migrations and test writes
would then act on production. Do not copy Docker's raw Postgres volume either; logical dumps are
portable, consistent, and let the local backend apply migrations added by the checkout.

### 6. Tear down or reset

Stop containers while retaining the restored data:

```powershell
docker compose -f compose.local.yml --env-file .env.local down
```

Return to a completely clean database (destructive to local data only). Because this is a bind
mount rather than a Docker named volume, stop the stack before deleting the project-local directory:

```powershell
docker compose -f compose.local.yml --env-file .env.local down
Remove-Item -LiteralPath ./.local/postgres-data -Recurse -Force
```

## App Catalog Development and Operations

The server-maintained App Catalog is the deployment-global mapping from platform evidence
(`AppIdentity`, such as `win:chrome` or `mac:com.google.chrome`) to the cross-platform `App`
product used by reports and knowledge. Its contract is defined by
[ADR-038](adr/038-server-maintained-app-catalog.md).

### Artifact and validation

The authoritative artifact is
`server/Heartbeat.Server/AppCatalog/app-catalog.json`. The server project copies it to build and
publish output; do not add a second Catalog under compose, environment variables, or the frontend.

- `schemaVersion` changes only when the JSON shape changes.
- `catalogVersion` increases when Catalog content changes. Editing content without increasing the
  version is detected as hash drift and prevents startup.
- Products sort by canonical `key`; every product's `identities` sort ordinally.
- Keys and identities must already be normalized. Duplicate product keys, duplicate identities,
  unknown schema versions, blank fields, products without identities, and unstable ordering are rejected.
- The canonical serializer fixes property order and emits deterministic UTF-8 bytes plus a trailing
  newline. Use it rather than hand-reserializing an exported candidate.

Run the contract tests before deploying a Catalog edit:

```powershell
dotnet test server/Heartbeat.Server.Tests --filter FullyQualifiedName~AppCatalogLoaderTests
dotnet test server/Heartbeat.Server.Tests --filter FullyQualifiedName~AppCatalogReconcilerTests
```

Startup loads and validates the artifact before serving requests. An invalid file, version/hash
drift, or failed reconciliation stops the backend; it does not continue with a partially applied
Catalog.

### Deployment administrator configuration

Catalog management changes every Owner's product mapping, so authorization uses an immutable JWT
`sub`, never the mutable username. Obtain the subject from the Auth platform and set it before
starting the stack:

```dotenv
ADMIN_SUBJECT=the-auth-platform-jwt-sub
```

Both compose files map this to `Administration__Subjects__0`. For a direct deployment, or after
adding more entries to the compose service's `environment` block, configure indexed ASP.NET values:

```dotenv
Administration__Subjects__0=first-sub
Administration__Subjects__1=second-sub
```

Restart the backend after changing the list. Heartbeat intentionally has no UI for granting or
revoking deployment administrators. `GET /api/v1/me` reports `isAdmin`; every administrator endpoint
also repeats the server-side subject check.

### Startup reconciliation and rollback compatibility

After EF migrations and knowledge backfills, startup takes the PostgreSQL advisory transaction lock
`heartbeat.app-catalog`. It applies active local Overrides first, then the built-in Catalog, updates
legacy compatibility references and protected consumers, writes the append-only audit, and advances
`AppCatalogStates` in the same transaction. Multiple backend replicas therefore serialize and
converge.

If the database has a newer applied `catalogVersion` than the binary, startup enters
`rollback-compatible` mode. Existing database mappings remain intact and the older artifact is not
reconciled. New Override writes and candidate export are disabled until a binary carrying an equal or
newer Catalog is deployed. The inventory remains readable and reports the rollback flag.

An equal version with a different hash is not rollback compatibility; it is invalid content drift and
startup fails. Do not repair either condition by editing `AppCatalogStates` manually.

### Promoting an Override into the repository

The Settings page creates deployment-local Overrides immediately, but neither an Override nor an
export changes `catalogVersion`. To upstream verified knowledge:

1. Select only the active Overrides suitable for every deployment. Private mappings remain
   unselected.
2. Export `app-catalog.v{N}.candidate.json`. It is the complete current Catalog plus the selected
   mappings, with proposed version `current + 1`.
3. Review the exact downloaded bytes and independently verify every native identity.
4. Replace `server/Heartbeat.Server/AppCatalog/app-catalog.json` with the reviewed candidate.
5. Run the Catalog tests and normal server tests, then commit the JSON through code review.
6. Deploy the backend. Only successful startup reconciliation records version `N` as applied and
   promotes matching local Overrides to inactive history.

There is deliberately no JSON import endpoint. Repeated exports before deployment keep the same
proposed version and do not mutate Catalog state, Overrides, audit rows, Apps, or AppIdentities.

### Read-only SQL diagnostics

For the local stack, open `psql` inside the database container:

```powershell
docker compose -f compose.local.yml --env-file .env.local exec db `
  psql -U heartbeat -d heartbeat
```

macOS/Linux:

```bash
docker compose -f compose.local.yml --env-file .env.local exec db \
  psql -U heartbeat -d heartbeat
```

Useful read-only queries:

```sql
-- Last successfully applied artifact and current startup mode.
SELECT "SchemaVersion", "CatalogVersion", "ContentHash", "AppliedAt", "StartupMode"
FROM "AppCatalogStates";

-- Recent reconciliation and Override history.
SELECT "Id", "EventType", "CatalogVersion", "ContentHash", "ActorSubject",
       "OccurredAt", "SummaryJson"
FROM "AppCatalogAudits"
ORDER BY "OccurredAt" DESC, "Id" DESC
LIMIT 50;

-- Active deployment-local intent. TargetAppKey remains the audit-safe product reference.
SELECT o."Id", i."Key" AS "IdentityKey", o."TargetAppKey", o."Status",
       o."CreatedBySubject", o."UpdatedBySubject", o."UpdatedAt"
FROM "AppCatalogOverrides" o
JOIN "AppIdentities" i ON i."Id" = o."AppIdentityId"
WHERE o."Status" = 'active'
ORDER BY i."Key";

-- Diagnose a split product while preserving the raw platform evidence.
SELECT i."Key" AS "IdentityKey", a."Id" AS "AppId", a."Key" AS "AppKey",
       a."DisplayName", a."IsProvisional"
FROM "AppIdentities" i
JOIN "Apps" a ON a."Id" = i."AppId"
WHERE i."Key" IN ('win:chrome', 'mac:com.google.chrome')
ORDER BY i."Key";
```

Use the administrator API for changes. Directly updating `AppIdentity.AppId`, Catalog state, or
Override rows bypasses reconciliation of historical compatibility rows, icons, knowledge, caches,
and audit.

## Running Tests

Server and shared tests use [Testcontainers](https://dotnet.testcontainers.org/) to spin up
a throwaway Postgres — **Docker Desktop must be running** or every DB-backed test fails
immediately with `DockerUnavailableException`:

```powershell
dotnet test                                        # everything
dotnet test server/Heartbeat.Server.Tests          # server services (needs Docker)
dotnet test collection/hub/Heartbeat.Collection.Hub.Tests                  # hub runtime (no Docker)
dotnet test collection/desktop/Heartbeat.Collector.System.Tests            # system Collector state machine (no Docker)
dotnet test collection/desktop/Heartbeat.Desktop.Windows.Tests             # Windows adapters (no Docker)
dotnet test collection/desktop/Heartbeat.Desktop.Mac.Tests                 # macOS adapters/head (no Docker)
dotnet test shared/Heartbeat.Core.Tests            # merger / validation / DateRange (no Docker)
```

The browser collector has its own vitest suite:

```powershell
cd collection/collectors/Heartbeat.Collector.Browser; npm test
```
