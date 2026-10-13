# Heartbeat.Observer.Sources

One durable, on-demand source collector with bounded local reads and offline native-format importers. This is not a live account sync service. The authoritative contracts, identities, fact times and exact limitations are in [Observers / Sources](../../Docs/Heartbeat.Docs/content/docs/observers/sources/index.mdx). The [100-case catalog](../../Docs/Heartbeat.Docs/content/docs/observers/sources/coverage.mdx) distinguishes real source assertions from partial, related-only and model-only cases. `scenario-coverage.json` is its machine-checkable mapping.

## Run from the repository root

Use the .NET SDK pinned in `global.json`:

```sh
dotnet run --project src/Observers/Heartbeat.Observer.Sources -- \
  --source slack-events \
  --input tests/Heartbeat.Model.Tests/NativeFixtures/Communication/slack-edit.json \
  --scope example-workspace \
  --state .local/observers/sources.sqlite --offline
```

`--offline` stores the complete parsed capture in local SQLite/Outbox without HTTP. Re-importing a saved capture reuses its evidence mapping. Keep the same state file and scope for retries. A new state file is a different collector instance.

| Source | Explicit input | Acquisition requirements |
| --- | --- | --- |
| `git` | Local repository with HEAD | Read access and Git on PATH; no fetch or hooks |
| `file` | One regular file or symlink | Linux GNU stat/readlink; no recursive scan or symlink traversal |
| `process` | Positive PID or `self` | Linux procfs access; no command-line, environment or memory collection |
| `graph-messages` | Native Graph Message or one list/delta page | Already authorized local response; no OAuth/pagination |
| `slack-events` | Native message create/change/delete event_callback | Saved native callback; no subscription/signature verification |
| `gmail-messages` | Native users.messages.get Message | Saved response; no account access |
| `gmail-drafts` | Native users.drafts.get Draft | Stable draft ID distinct from replaceable Message ID |
| `gmail-history` | One successful users.history.list page | Completeness explicitly bounded; no cursor repair |
| `otlp-json` | One signal's OTLP JSON export | Traces/logs/gauge/sum; no HTTP/gRPC/protobuf receiver |
| `cloudevents-json` | Structured 1.0 object or batch | No binary HTTP binding |
| `debezium-json` | PostgreSQL c/u/d/r envelope | No Kafka key/header/offset/tombstone acquisition |
| `icalendar-ics` | UTF-8 ICS VEVENT subset | No calendar login or recurrence expansion |
| `gpx-xml` | GPX 1.1 waypoint/track file | No GPS device access; routes rejected |

File inputs are bounded to 8 MiB. The host buffers at most 10,000 mapped records and a bounded property payload before committing the complete capture transaction. Sources have additional depth/decoded-field limits. Unknown fact times stay unknown. Import failures do not commit a partial capture.

The default import capture key is the file SHA-256. Live local snapshots receive a fresh UUID key. `--capture-key` explicitly identifies a capture/retry; each adapter documents where native event/revision identities supersede it. Scope must be a stable account/repository/device/feed identifier, never an access token.

## Send existing outbox entries

Start the repository's normal local environment using [AppHost](../../../tools/Heartbeat.AppHost/README.md), then run separately:

```sh
dotnet run --project src/Observers/Heartbeat.Observer.Sources -- \
  --state .local/observers/sources.sqlite \
  --api http://localhost:8080/api/ --drain
```

Omit `--offline` on a capture to attempt immediate sending. The sender performs GET/check or PUT and then another GET pass; PUT acknowledgement alone does not remove an outbox entry. Network failure or conflicting remote content leaves local work retryable. Schema display-name-only changes are accepted and preserved; the collector-owned Observer display name is currently exact, so a manual remote rename causes a reported conflict rather than being overwritten.

JSON stdout includes observer ID, capture receipts, pending count and recorded errors. Exit 0 means offline persistence succeeded or online outbox is empty; exit 3 means pending delivery remains. Invalid inputs/commands fail; cancellation leaves previously durable records available.

Protect and back up the SQLite state: it contains persistent UUID/evidence mappings and pending payloads. Losing it loses replay continuity. After acknowledgement, the backend retains submitted historical content; SQLite does not keep every acknowledged payload as a second complete archive. No backup/GC/retention system is implemented.

## Verification

```sh
dotnet test --project tests/Heartbeat.Model.Tests/Heartbeat.Model.Tests.csproj
dotnet test --project tests/Heartbeat.Integration.Tests/Heartbeat.Integration.Tests.csproj
```

Native fixtures are explicit synthetic API/XML/ICS payloads in `tests/Heartbeat.Model.Tests/NativeFixtures/`; local tests create isolated real files/repos and read actual procfs. `SourcePipelineTests` exercises native input → SQLite restart/replay → actual API/PostgreSQL → full entity readback. Full source acquisition, production account authorization, platform coverage and runtime boundaries remain separately documented in [verification](../../Docs/Heartbeat.Docs/content/docs/observers/sources/verification.mdx).
