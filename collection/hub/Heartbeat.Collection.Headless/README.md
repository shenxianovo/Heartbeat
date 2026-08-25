# Heartbeat Headless Hub

The headless Hub runs the same authentication, durable Collector inbox, segment buffer, cache,
and upload streams as the desktop Agent, without UI, foreground-window APIs, or release-vendor
dependencies. Its operational machine identity is used only for Hub authentication/status; the
configured Account remains the Collector Fact Subject.

Build a deterministic local reference Package:

```bash
dotnet build ../../collectors/Heartbeat.Collector.Reference.ManagedProcess/Heartbeat.Collector.Reference.ManagedProcess.csproj
../../collectors/Heartbeat.Collector.Reference.ManagedProcess/bin/Debug/net10.0/Heartbeat.Collector.Reference.ManagedProcess \
  --create-package ./reference-package
```

Create `heartbeat-headless.json` (paths are resolved relative to this file):

```json
{
  "apiKey": "replace-me",
  "dataDirectory": "./data",
  "packageDirectory": "./reference-package",
  "subjectId": "0198d5df-5df3-70a1-937d-68a7d64623e2",
  "subjectKind": "account",
  "hubHardwareId": "headless:my-server",
  "hubName": "My server Hub",
  "uploadIntervalSeconds": 60,
  "configSchemaVersion": 1,
  "config": {},
  "startupTimeoutSeconds": 30,
  "drainGraceSeconds": 10
}
```

Run it:

```bash
dotnet run --project Heartbeat.Collection.Headless.csproj -- ./heartbeat-headless.json
```

For local ingest verification, `HEARTBEAT_API_BASE_URL` can override the Analytics endpoint.
SIGINT/SIGTERM sends `activation.drain`, waits the configured grace period, terminates an
unresponsive child, then lets the upload worker perform its final drain.
