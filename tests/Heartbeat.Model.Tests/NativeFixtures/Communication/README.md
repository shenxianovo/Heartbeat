# Native communication fixtures

These are **synthetic native API response/event payloads**, authored from the public
Microsoft Graph and Slack contracts. They are not recorded private account data and
are not normalized scenario JSON. All accounts, addresses, IDs and tokens are fake.
The Graph fixture deliberately places two versions and a removal in one page to
exercise repeated native identity; it does not assert that a production Graph server
will deliver those versions in that order. Import order is not causal ordering.

- `graph-delta.json`: Graph message delta `value` envelope; draft revisions,
  projected fields, text/HTML body, distinct sender/from, and folder-removal tombstone
- `slack-{create,edit,delete}.json`: Slack Events API `event_callback` envelopes,
  `message_changed` nested `message`, `previous_message`, `deleted_ts`, microsecond timestamps

Source contracts:
- https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/message-delta?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/outlook-immutable-id
- https://docs.slack.dev/apis/events-api/
- https://docs.slack.dev/reference/events/message/message_changed/
- https://docs.slack.dev/reference/events/message/message_deleted/
