# Native telemetry file adapters

These are bounded offline importers, not live receivers or subscriptions. `SourceReadContext.Input`
is a local JSON file. The common reader limits inputs to 8 MiB, rejects duplicate decoded keys,
and honors cancellation. Files in `tests/Heartbeat.Model.Tests/NativeFixtures/Telemetry` are
synthetic native-format examples, not captured production data or normalized model fixtures.

## Supported formats

- `OtlpJsonSource` / `otlp-json`: one OTLP JSON export request containing `resourceSpans`,
  `resourceLogs`, or `resourceMetrics`. Traces emit span, event, and ordered link records;
  duplicate links remain distinct and both endpoints are references. Remote parents may remain
  unresolved. Logs retain event and observed clocks separately; observed time never substitutes
  for absent event time. Gauge and sum points expose units, numeric values, temporality and
  monotonicity. Int64 measurements remain decimal strings to avoid floating-point loss.
- `CloudEventsJsonSource` / `cloudevents-json`: CloudEvents 1.0 structured JSON objects or batch
  arrays. Identity is `(source,id)`, not time. JSON `data` and base64 `data_base64` are retained;
  mutually present data encodings are rejected. Extension values remain typed attribute entries.
- `DebeziumJsonSource` / `debezium-json`: PostgreSQL `c/u/d/r` envelopes, directly or under Kafka
  Connect `payload`, singly or in an offline array. Before/after images, WAL position, transaction
  identity/order, source clock and connector processing clock remain distinct. Snapshot rows
  lacking WAL position use capture-local identity; change records otherwise require `source.lsn`.
  No row identity is invented from an arbitrary `id` column.

Native JSON members are preserved as schema-declared arrays of `{name,type,valueJson}` entries;
`valueJson` is a lossless JSON text string, not an undeclared schema object. Resource and scope
objects are additionally preserved as JSON strings. Record-level extensions and OTLP export/resource/scope wrapper metadata are retained as typed
attribute entries; unknown members are preserved without inventing domain meaning. Kafka Connect schema declarations are not used as
Heartbeat schemas. The fixed adapter schemas include `sourceEntityId` for runtime identity mapping.

## Time and identity boundaries

Nanosecond decimals and RFC3339 source strings remain exact in content. The `DateTimeOffset`
projection truncates sub-100 ns precision; persistence may additionally project to microseconds.
OTLP zero timestamps mean unknown. Debezium source zero timestamps are treated as unavailable.
No clock is replaced with import time and no IANA timezone is inferred from an offset.
A span's event index and link index are source-array positions, not chronological sorting.

Runtime scope is required and scopes every identity. Span identity uses trace/span IDs; a canonical native revision fingerprint distinguishes active/ended or enriched exports while identical retries replay;
CloudEvents replay by source/event IDs. Logs and metric points have no mandatory native event ID,
so their identities use the persisted capture key plus resource/scope/record/point positions.
Debezium event identity uses source database, native transaction metadata ID and total order when
both are present. A WAL position alone cannot distinguish all row changes; without transaction
ordering, each capture-array occurrence has its own identity, including byte-identical occurrences.
The canonical native-envelope revision fingerprint distinguishes changed processing metadata. No
Kafka offset is invented. The importer does not claim cross-capture deduplication without native
ordering, Kafka-offset exactly-once semantics, or transaction-atomic persistence.

## Explicitly unsupported

OTLP protobuf, HTTP/gRPC receiving, legacy instrumentation-library JSON field names, multiple
signal types in one file, histogram/exponential-histogram/summary metrics, nonfinite double
measurements, and standalone Prometheus/Sentry/provider APIs are unsupported. Unsupported metric
kinds fail rather than silently disappear. Native data nested inside supported records is retained
but not fully semantically validated as a complete OTLP protobuf implementation would be.

CloudEvents HTTP binary binding, XML/Avro/Protobuf encoding and versions other than 1.0 are not
supported. RFC3339 leap seconds cannot be projected by .NET and are rejected.

Debezium connectors other than PostgreSQL, Kafka keys/headers/offsets, tombstones (JSON null),
transaction boundary messages, heartbeats, schema-change messages, truncate/logical-decoding
messages, and primary-key-transition correlation are unsupported. A delete envelope is supported;
a Kafka tombstone cannot be attributed without its surrounding Kafka record and is rejected.
Before/after row images do not by themselves establish primary-key continuity.

## Verification linkage

`TelemetrySourceTests` directly parses the native fixtures and asserts semantics associated with
B06–B10 (spans, links, precision, ordering, missing telemetry), B22 (log clocks), C23–C24
(metric windows/points), D01 (CDC ordering and clocks; transaction references only, no row identity
or transaction-atomic sink claim), D02 (delete versus unsupported tombstone), and D20 (CloudEvents
identity). This is bounded, sometimes partial coverage of those model scenarios, not a claim that
all 100 model cases now have a production source integration. D03 primary-key transitions remain
model-only. Additional tests reject wrong shapes, missing required IDs, malformed timestamps and
pre-cancelled reads.

## Source contracts

- [OTLP JSON mapping](https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding)
- [Trace protobuf fields](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/trace/v1/trace.proto)
- [Metrics protobuf fields](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/metrics/v1/metrics.proto)
- [Logs protobuf fields](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/logs/v1/logs.proto)
- [CloudEvents JSON format](https://github.com/cloudevents/spec/blob/main/cloudevents/formats/json-format.md)
- [Debezium PostgreSQL envelopes](https://debezium.io/documentation/reference/stable/connectors/postgresql.html#postgresql-events)
