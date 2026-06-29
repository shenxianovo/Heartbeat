# ADR-013: Re-enable Auto-Migration in All Environments

## Status: Accepted

## Date: 2026-06-29

(pending commit)

## Context

[ADR-007](./007-disable-prod-auto-migration.md) restricted `db.Database.Migrate()` to the Development environment, requiring a manual `dotnet ef database update` for every other environment. That decision was made with a multi-environment, review-gated production deployment in mind.

In practice Heartbeat is a **single-user, self-hosted** deployment. The risks ADR-007 guarded against do not apply here:

- There is no shared production database needing change review — the operator and the user are the same person.
- Downtime from a blocking migration is irrelevant for a personal service.
- Schema changes ship together with the code that needs them; a deploy without the matching migration is simply broken.

The concrete failure that triggered this: a new `InputEvents` table was added (ADR-012), the server was deployed with the new endpoints, but the migration was never applied manually — so `POST /input-events` returned 500 (`relation "InputEvents" does not exist`) while older endpoints kept working. The manual-migration step is easy to forget and provides no value in this deployment model.

## Decision

Removed the `IsDevelopment()` guard around `db.Database.Migrate()` in `Program.cs`. Migrations are now applied automatically on startup in **all** environments.

This supersedes ADR-007.

## Consequences

- ✅ A deploy always brings the database schema in sync with the code — no forgotten manual step.
- ✅ Matches the single-user self-hosted reality; one less operational footgun.
- ⚠️ A faulty migration applies on startup with no review gate. Acceptable for a personal deployment; mitigated by the migration being exercised in the Postgres-backed test suite (see ADR-012) before deploy.
- ⚠️ A long-running migration blocks app startup. Not a concern at this data scale.
- ⚠️ If Heartbeat ever grows into a multi-tenant or team deployment, this decision should be revisited.

## References

- [`server/Heartbeat.Server/Program.cs`](../../server/Heartbeat.Server/Program.cs) — unconditional migration on startup
- [ADR-007](./007-disable-prod-auto-migration.md) — superseded by this decision
