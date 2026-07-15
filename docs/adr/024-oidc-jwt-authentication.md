# ADR-024: OIDC/JWT Authentication via External Auth Platform

## Status: Accepted

## Date: 2026-07-14

[`b4801dd`](https://github.com/shenxianovo/heartbeat/commit/b4801dd) — feat: migrate Heartbeat auth from custom ApiKey to AuthService JWT
[`80cb0d1`](https://github.com/shenxianovo/heartbeat/commit/80cb0d1) — feat(frontend): migrate login to OIDC authorization code + PKCE
[`6c675a6`](https://github.com/shenxianovo/heartbeat/commit/6c675a6) — feat(server): accept both OIDC access tokens and Agent session JWTs

Supersedes [ADR-004](./004-apikey-header-authentication.md).

## Context

ADR-004 authenticated every request with a custom `Authorization: ApiKey {key}`
header: the server looked the key up in `Devices` on each call and wrote the
`DeviceId` into the `ClaimsPrincipal`. That decision explicitly rejected JWT/OAuth
as "overkill for a single-user personal project with device-level auth."

Two things changed that reversal calculus:

- **The data-isolation invariant** (CONTEXT-MAP positioning #2 — "数据模型不焊死多租户").
  Keeping the multi-user door open means identity has to be an **owner**, not a
  device. A device-scoped ApiKey can't express "this user owns these devices"; a
  per-request DB lookup on a plaintext key is also a hot-path cost and a
  plaintext-secret exposure.
- **The Dashboard needs a login.** A browser SPA can't hold a device ApiKey. Once
  the frontend needs real user login, standing up a second, parallel auth
  mechanism for it (while the Agent keeps ApiKey) is more surface than adopting
  one scheme for both.

An **external self-hosted Auth platform** already existed (email/Google/GitHub
login, OIDC discovery, RSA/JWKS). Adopting it means neither building an
IdP in-repo nor keeping the bespoke ApiKey handler.

The wrinkle: the two clients can't obtain tokens the same way.

- The **Dashboard** is a public browser client — it runs the OIDC authorization
  code + PKCE flow interactively and receives an **OIDC access token**
  (`typ=at+jwt`).
- The **desktop Agent** is headless — there's no interactive browser login on a
  background tray app. It holds a long-lived **ApiKey** (now demoted to a
  credential the user pastes once) and exchanges it at
  `POST /api/v1/apikeys/exchange` for a short-lived **session JWT** (`typ=JWT`).

Both tokens are signed by the same Auth platform under the same JWKS, but they
are **not interchangeable**: they differ in `typ`, and their issuer/audience
claims are shaped differently (the OIDC issuer carries a trailing slash, the
session issuer/audience do not; the OIDC access token currently carries no `aud`
because the upstream registers no resource).

## Decision

**Adopt the external Auth platform as the single IdP; the server accepts two
Bearer token families side by side, routed by JWT `typ`.**

Server side ([`Program.cs`](../../server/Heartbeat.Server/Program.cs)):

- A `TokenSelector` policy scheme sniffs the JWT `typ` header
  (`JwtTypeSniffer.IsOidcAccessToken`) and forwards to one of two `JwtBearer`
  schemes:
  - **`OidcBearer`** — validates `ValidTypes = ["at+jwt"]` against the OIDC
    issuer. Because the upstream issues no `aud`, audience validation is off by
    default; a compensating `OnTokenValidated` check rejects any token whose
    `client_id` isn't this app's, so tokens the same IdP minted for other
    downstream apps are refused.
  - **`SessionBearer`** — validates the Agent session JWT against the
    (no-trailing-slash) issuer/audience with full audience validation.
- Both schemes auto-discover signing keys from
  `{Authority}/.well-known/openid-configuration`. No key material lives in this
  repo.

Client side:

- Agent: [`TokenManager`](../../desktop/Heartbeat.Agent/Http/TokenManager.cs)
  exchanges the ApiKey for a session JWT, caches it, and refreshes 60s before
  expiry; [`BearerTokenHandler`](../../desktop/Heartbeat.Agent/Http/BearerTokenHandler.cs)
  injects `Authorization: Bearer {jwt}` plus `X-Hardware-Id` / `X-Device-Name`.
- Dashboard: [`stores/auth.ts`](../../frontend/src/stores/auth.ts) runs the PKCE
  flow and stores the access token + refresh token.

**Device identity moves off the credential.** The `Devices.ApiKey` column is
dropped (migration `20260513084042_RemoveDeviceApiKey`). A device is now
identified by `(OwnerId, HardwareId)` — `OwnerId` from the JWT `sub`, `HardwareId`
from the `X-Hardware-Id` header (Windows MachineGuid). The server resolves or
auto-creates the device from that pair (`DeviceService.ResolveByHardwareIdAsync`).
See `shared/CONTEXT.md` for the Device glossary entry.

## Consequences

- ✅ One IdP for both clients; login, refresh, and social providers are the Auth
  platform's problem, not this repo's.
- ✅ Identity is owner-scoped, keeping the multi-user door open without a
  migration (CONTEXT-MAP invariant #2).
- ✅ No plaintext secret on the wire per request; no per-request device DB lookup
  in the auth path (JWT is validated against cached JWKS).
- ✅ The two token families are validated **precisely** rather than with one
  loose ruleset — an OIDC token can't be replayed as a session token and vice
  versa.
- ⚠️ The `typ`-sniffing policy scheme is non-obvious machinery; a reader seeing
  two `JwtBearer` registrations needs this ADR to know why. The dual issuer/aud
  shapes are an upstream quirk this code compensates for, not a design we chose.
- ⚠️ The OIDC `client_id` check is a stand-in for absent audience validation;
  if the upstream later registers a resource and issues `aud`, flip
  `OidcAudience` on and the check becomes redundant.
- ⚠️ The Agent still stores a long-lived ApiKey at rest (in config) — it's no
  longer sent per request, but it's still the root credential the exchange
  depends on.

## References

- [`server/Heartbeat.Server/Program.cs`](../../server/Heartbeat.Server/Program.cs) — dual-scheme registration + `typ` selector
- [`desktop/Heartbeat.Agent/Http/TokenManager.cs`](../../desktop/Heartbeat.Agent/Http/TokenManager.cs) — ApiKey → session JWT exchange + caching
- [`desktop/Heartbeat.Agent/Http/BearerTokenHandler.cs`](../../desktop/Heartbeat.Agent/Http/BearerTokenHandler.cs) — bearer + device headers injection
- [`frontend/src/stores/auth.ts`](../../frontend/src/stores/auth.ts) — OIDC authorization code + PKCE
- `shared/CONTEXT.md` — Device / ApiKey glossary entries
- [ADR-004](./004-apikey-header-authentication.md) — the superseded ApiKey scheme
