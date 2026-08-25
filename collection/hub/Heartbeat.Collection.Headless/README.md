# Heartbeat Headless Hub

The headless Hub runs the same authentication, durable Collector inbox, segment buffer, cache,
and upload streams as the desktop Agent, without UI, foreground-window APIs, or release-vendor
dependencies. The legacy Analytics header adapter is bound to the configured Account Subject;
the server machine hosting the Hub Instance is never written as the Fact Subject.

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
  "subjectName": "My reference account",
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
