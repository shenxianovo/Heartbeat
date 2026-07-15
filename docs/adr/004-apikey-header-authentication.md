# ADR-004: Custom ApiKey Authentication via HTTP Header

## Status: Superseded by [ADR-024](./024-oidc-jwt-authentication.md)

> ApiKey authentication has been fully removed: the `ApiKey` column was dropped
> (migration `20260513084042_RemoveDeviceApiKey`), and neither
> `ApiKeyAuthenticationHandler` nor `ApiKeyDelegatingHandler` exist anymore.
> The current OIDC/JWT design — dual token families, PKCE login, `X-Hardware-Id`
> device resolution — is documented in [ADR-024](./024-oidc-jwt-authentication.md).
> The sections below describe the original (now-defunct) ApiKey design and are kept for historical context.

## Date: 2026-03-04

[`877851d`](https://github.com/shenxianovo/heartbeat/commit/877851d) — refactor: move ApiKey from DTOs to request header, update related services for authentication

## Context

Initially, every upload DTO (usage, status, icon) carried an `ApiKey` field in the JSON body. The server extracted it manually in each controller action to identify the device. This had several issues:

- **DTO pollution**: Authentication concern leaked into every request/response model.
- **Inconsistency**: Each controller duplicated the "extract key → lookup device" logic.
- **No standard auth pipeline**: Couldn't leverage ASP.NET Core's `[Authorize]` attribute or middleware.

Alternatives considered:

1. **Keep ApiKey in body**: Simple, no middleware needed, but violates separation of concerns.
2. **JWT / OAuth**: Industry standard, but overkill for a single-user personal project with device-level auth.
3. **Custom `Authorization: ApiKey {key}` header** with ASP.NET Core `AuthenticationHandler`: Leverages the built-in auth pipeline, moves auth out of DTOs, supports `[Authorize]` attribute.

## Decision

Adopted a **custom `AuthenticationHandler<AuthenticationSchemeOptions>`** that reads `Authorization: ApiKey {key}` from the header, looks up the device in the DB, and writes the `DeviceId` into `ClaimsPrincipal`. Controllers use `[Authorize]` and read the device ID from claims.

On the client side, an `ApiKeyDelegatingHandler` injects the header into every outgoing `HttpClient` request automatically.

## Consequences

- ✅ Clean DTOs: no authentication fields in request bodies.
- ✅ Centralized auth: one handler, one place to change.
- ✅ Standard ASP.NET Core pattern: `[Authorize]`, claims, middleware pipeline.
- ⚠️ Every authenticated request triggers a DB query (`SELECT * FROM Devices WHERE ApiKey = @key`). Could add caching later if needed.
- ⚠️ ApiKey is sent as plaintext in the header — acceptable over HTTPS but not suitable for public-facing APIs.

## References

- [`server/Heartbeat.Server/Authentication/ApiKeyAuthenticationHandler.cs`](../../server/Heartbeat.Server/Authentication/ApiKeyAuthenticationHandler.cs) — server-side auth handler
- [`desktop/Heartbeat.Agent/Http/ApiKeyDelegatingHandler.cs`](../../desktop/Heartbeat.Agent/Http/ApiKeyDelegatingHandler.cs) — client-side header injection
- [`server/Heartbeat.Server/Program.cs`](../../server/Heartbeat.Server/Program.cs) — auth scheme registration
