# Heartbeat Headless Hub

The headless Hub runs the same authentication, durable Collector inbox, segment buffer, cache,
and upload streams as the desktop Agent, without foreground-window APIs or release-vendor
dependencies. One Collector Runtime hosts every configured Instance; each Instance keeps an
independent upload identity, cache, and encrypted secret namespace. A waiting login never blocks
the other configured instances.

The owner-facing management API is served at `/hub/api/v1`. It accepts only an OIDC access token
whose `sub` and `client_id` match this Hub's configuration. The Dashboard calls it directly through
the same-origin reverse proxy; credentials and reusable VRChat sessions never traverse Analytics.

Build a deterministic local reference Package:

```bash
dotnet build ../../collectors/Heartbeat.Collector.Reference.ManagedProcess/Heartbeat.Collector.Reference.ManagedProcess.csproj
../../collectors/Heartbeat.Collector.Reference.ManagedProcess/bin/Debug/net10.0/Heartbeat.Collector.Reference.ManagedProcess \
  --create-package ./reference-package
```

Build a VRChat Package:

```bash
dotnet build ../../collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj
../../collectors/Heartbeat.Collector.VRChat/bin/Debug/net10.0/Heartbeat.Collector.VRChat \
  --create-package ./vrchat-package
```

Create `heartbeat-headless.json` (paths are resolved relative to this file). `instances` may contain
multiple accounts and, later, other managed Account Collector Packages:

```json
{
  "apiKey": "replace-me",
  "dataDirectory": "./data",
  "uploadIntervalSeconds": 60,
  "listenUrl": "http://0.0.0.0:8082",
  "management": {
    "ownerSubject": "the-owner-oidc-sub",
    "authority": "https://auth.example.com",
    "issuer": "https://auth.example.com/",
    "clientId": "heartbeat-web",
    "audience": null,
    "requireHttpsMetadata": true
  },
  "instances": [
    {
      "instanceKey": "vrchat-alice",
      "packageDirectory": "./vrchat-package",
      "subjectId": "0198d5df-5df3-70a1-937d-68a7d64623e2",
      "subjectKind": "Account",
      "subjectName": "VRChat · Alice",
      "configSchemaVersion": 1,
      "config": { "pollIntervalSeconds": 60 },
      "startupTimeoutSeconds": 30,
      "drainGraceSeconds": 10
    }
  ]
}
```

Run it:

```bash
dotnet run --project Heartbeat.Collection.Headless.csproj -- ./heartbeat-headless.json
```

For the Compose stacks, copy `heartbeat-headless.compose.example.json` to
`.local/heartbeat-headless.json`, replace the API key, owner `sub`, and Subject ID, then run:

```bash
docker compose -f compose.local.yml --profile headless up --build
```

The `headless` profile builds the Hub and bundled VRChat Package, mounts persistent `/data`, and
joins the frontend network as the nginx upstream named `headless`. Set `HEADLESS_CONFIG_PATH` when
the configuration lives elsewhere. Production `compose.yml` exposes the same opt-in profile.

For local ingest verification, `HEARTBEAT_API_BASE_URL` can override the Analytics endpoint.
Set `HEARTBEAT_VRCHAT_MOCK=1` for the offline flow: username `test-user`, password
`test-password`, and verification code `123456`. Vite proxies `/hub` to `127.0.0.1:8082`;
the production nginx configuration expects the Hub at `headless:8080` on its container network.

Without `HEARTBEAT_VRCHAT_MOCK`, the package uses the real VRChat API. That path is deliberately a
manual smoke test: open the owner's Dashboard, press **登录** under the Account Subject, and finish
the credentials/verification steps in the browser. VRChat does not provide a supported OAuth flow
for this integration, so this collector remains experimental and must never be presented as an
official VRChat integration.

SIGINT/SIGTERM sends `activation.drain`, waits the configured grace period, terminates an
unresponsive child, then lets the upload worker perform its final drain.
