# Keep interactive Collector authorization Hub-local

Collectors that require a third-party account use optional `auth.interactive/1` and
`secrets.instance/1` Collector Protocol capabilities owned by the Hub Instance. An authorization
challenge moves only that Collector Instance into `WaitingForAuthorization`; the process remains
alive and all other Instances continue independently. A credentials or verification-code challenge
declares its fields, receives one matching in-memory response, and ends with `auth.completed`.

The owner-facing Dashboard renders the challenge as a Subject-scoped recovery action and calls the
Hub directly through same-origin `/hub/api`. The Hub validates the existing Heartbeat OIDC access
token against the configured owner `sub` and web `client_id`. Analytics does not relay credentials,
authorization responses, secrets, status, or management commands. A successful login writes only
reusable session material to the Hub's per-Collector-Instance encrypted secret store; credentials
and verification codes are not written to Manifest, Fact, Runtime State, outbox, or ordinary logs.

Headless configuration is a list of preconfigured Instances. One Hub Runtime owns all of them; each
Instance gets its own durable state, upload identity, cache, and secret namespace. This preserves
the legacy Analytics identity boundary while allowing one deployment to host multiple Account,
Machine, or Person Subjects. Dynamic Instance creation is intentionally outside this decision.

VRChat is an experimental adapter: VRChat does not expose a supported third-party OAuth flow and
its current creator guidance discourages third parties from collecting credentials or sessions.
The UI therefore does not pretend to redirect through OAuth, and the real-account path remains an
explicit manual smoke test. The offline mock is the automated contract. Until local end-to-end data
inspection establishes a stable semantic split, the adapter preserves the raw VRChat instance string
in both identity and payload instead of parsing access type, region, or group metadata.
