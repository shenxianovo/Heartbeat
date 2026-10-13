# Offline Gmail native response importers

These adapters read saved Gmail REST response JSON. They do not authenticate, access an account, issue network requests, send mail, download attachments, fetch another page, or repair expired synchronization cursors. Synthetic fixtures use the native response shape; they are not real account captures.

- `gmail-messages`: one `users.messages.get` Message resource (FULL, METADATA, MINIMAL, or RAW fields when supplied)
- `gmail-drafts`: one `users.drafts.get` Draft resource with its nested Message
- `gmail-history`: one successful `users.history.list` response page

Pass an explicit account scope and stable capture key to the collector. `me` alone is not a globally meaningful account identity. Message IDs, draft IDs, and thread IDs stay account-scoped native data; the durable source store maps them to internal UUIDv7 identities. A draft uses `draft.id`, with a reference to the replaceable nested message identity. A sent message must not be inferred to be the same draft from subject or headers.

Message and draft snapshots preserve supplied label IDs, history ID, internalDate, MIME parts, header order and duplicate header names. MIME parts are flattened in preorder with positional `path` and `parentPath`, retaining nesting independently of part IDs. Body `data` and `raw` remain validated base64url; no charset guessing, HTML execution, or RFC message re-parsing occurs. An `attachmentId` is a retrieval reference only, not evidence that bytes were downloaded. Omitted fields remain omitted. Workspace classification labels and field selections are retained separately from mailbox labels. Unsupported fields in message snapshots or their MIME structures cause rejection instead of silent loss. History records project change semantics and message identity, not full message snapshots.

Snapshot observations have unknown fact time: the native response has no capture timestamp, and an import timestamp must not masquerade as the time the state was seen. `internalDate` is retained as source data for the message's internal creation time; it does not date label changes or draft edits. The Date header remains a separate header value. Independent captures receive independent observation evidence, and replay of the same saved capture is stable.

History uses each specific change type, preserving all four native operations. The generic `messages` collection is used only for otherwise unrepresented message IDs, with `unspecified-change`; its duplicate appearances do not invent extra events. A history sequence ID orders changes but is not a timestamp. History changes have unknown fact time and replay independently of capture. A page observation explicitly records whether more pages are reported, never persists the continuation token, and never asserts a complete mailbox sync, even for a terminal page. Successful pages cannot prove cursor expiry. Native error payloads are rejected with no observations; a 404 can require full sync according to Gmail, but this importer neither diagnoses its cause nor repairs it.

Input bounds, cancellation, depth limits and duplicate decoded JSON-field detection come from the shared native JSON reader. No envelope credentials are persisted. MIME header/body content is intentionally source data and may itself be sensitive.

## Primary-source semantics

Verified against Google's REST documentation:

- [Message and MIME structure](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.messages)
- [MessagePartBody and attachment retrieval](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.messages.attachments)
- [Draft container and message replacement](https://developers.google.com/workspace/gmail/api/guides/drafts)
- [History page, change types, pagination and invalid cursor](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.history/list)

The native tests provide partial evidence for baseline cases A08 (successful history page, not expiry recovery), A09 (draft replacement, not actual sending), and A10 (MIME structure and attachment reference, not separately downloaded bytes). They do not establish live Gmail integration.
