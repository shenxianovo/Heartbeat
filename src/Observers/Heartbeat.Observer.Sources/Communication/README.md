# Offline native communication importers

`graph-messages` reads one saved Microsoft Graph message JSON object or one native
list/delta response page (`value`). `slack-events` reads one saved Slack Events API
`event_callback` envelope containing a standard message, `message_changed`, or
`message_deleted`. These adapters only read local files. They do not authenticate,
fetch, poll, maintain OAuth tokens, acknowledge Slack deliveries, verify request
signatures, advance delta checkpoints, or infer that a mailbox export is complete.
A Graph page with `@odata.nextLink` is still only one page. Supply each separately.
Cursor URLs and unrelated authorization/envelope fields are not persisted. The
adapters do not store access tokens, request headers or whole raw source payloads.
Message body/address text is source content, however, and may itself contain private
information; use only files authorized for this import.

## Identity and evidence

Provide a stable account scope for Graph and a stable installation/account scope for
Slack. Graph native identity is the scope plus case-sensitive message `id`. Default
Graph IDs can change after a folder move. Use captures requested with
`Prefer: IdType="ImmutableId"` for move-stable IDs within the same mailbox; the JSON
body alone cannot prove this header was used. Archive mailbox moves/export-reimport
can still change immutable IDs. `internetMessageId`, subject, sender address, and
conversation ID are never substituted for message identity. A draft revision using
the same Graph ID remains the same source identity; ID replacement cannot be merged
without source-supported mapping.

Slack message identity includes scope, workspace `team_id`, channel and exact `ts`.
Event ID provides replay evidence independently of message identity. Edit/delete
snapshots retain the same message identity and distinct event evidence. Previous
text is preserved if the event supplies it. Thread roots, channels and users are
explicit references; their existence is not asserted or manufactured.

Graph change keys plus canonical record hash identify a version's evidence across
page reordering and repeated captures. Unversioned records, including tombstones,
use capture key plus native identity and canonical record hash. Field selection can
produce distinct evidence for the same change key; no claim of a new server revision
is made. The shared runtime preserves immutable historical content per evidence.

## Fact time and limits

Graph observations express the message revision at `lastModifiedDateTime`. Missing
revision time stays unknown, even if `receivedDateTime`, `sentDateTime` or import time
is available. Delta `@removed` means removal from the queried collection, potentially
a folder move, not necessarily deletion from the mailbox. Its fact time is unknown.
The query's folder cannot be recovered reliably from a tombstone alone.

Slack creation time is message `ts`, edit time is `message.edited.ts` (or native
`event.event_ts` when absent), and deletion time is `event.event_ts`. Envelope
`event_time` is not used as fact time. Unknown edit/delete time remains null. Instant
facts have equal known bounds; source IANA timezone is unknown. Decimal parsing
preserves microseconds, avoiding floating-point timestamp rounding.

Inputs are limited to 8 MiB, depth 32, and 262,144 UTF-16 characters per decoded
string; excess, duplicate JSON keys, malformed selected fields, or unsupported Slack
subtypes fail explicitly. No body rendering, attachment decoding, remote links,
base64/MIME parsing, or truncation is performed. Cancellation is checked during reads
and record iteration. Graph selected-field omission remains omission; it is not
converted to a fabricated empty value. HTML remains untrusted literal text.

## Unsupported by these two communication adapters

Gmail messages/history/drafts are handled separately by the `Gmail` module, and ICS by
`PhysicalCalendar`; they are not supported by these two adapters. Graph batch responses, notifications, attachment
content and live synchronization; Slack Web API history/export, bot/system message
subtypes, files/reactions/blocks and signature verification; ICS/calendar import,
recurrence expansion, and calendar cancellation semantics. No calendar parser is
claimed in this module. Unsupported formats need dedicated source adapters rather
than relabeled generic JSON.

Synthetic fixture provenance and primary specifications are in
`tests/Heartbeat.Model.Tests/NativeFixtures/Communication/README.md`.

## Executable semantic coverage

`CommunicationSourceTests` covers A11 partially (immutable Graph IDs across folder
moves, with no guessed mapping for default ID replacement), A12 (body/flag revisions
and collection removal), A13 (Slack changes arriving before creation, durable replay
and historical text), and A14 (deletion without a captured original). This does not
turn the other communication/calendar scenarios into implemented adapters. Tests
also exercise model schema validation, account/workspace/channel collisions,
selected-field omission versus null, exact microsecond times, unknown fact time,
reordered Graph pages, native event replay, input limits and cancellation. All
fixtures are synthetic. No production account integration has been exercised.
