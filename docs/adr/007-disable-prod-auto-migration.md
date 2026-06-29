# ADR-007: Disable Auto-Migration in Production

## Status: Superseded by ADR-013

## Date: 2026-03-19

[`0bcc0db`](https://github.com/shenxianovo/heartbeat/commit/0bcc0db) — fix(db): disable auto migration in production environment

## Context

During early development, the server called `db.Database.Migrate()` at startup to automatically apply pending EF Core migrations. This was convenient for rapid iteration but risky for production:

- **Unintended schema changes**: A deploy with a new migration could alter tables on a live database without review.
- **Downtime risk**: Long-running migrations (e.g., adding indexes) would block app startup.
- **No rollback path**: Auto-migration doesn't support easy rollback if a migration fails halfway.

Alternatives:

1. **Always auto-migrate**: Convenient but dangerous in production.
2. **Never auto-migrate**: Requires manual `dotnet ef database update` for every environment, including dev.
3. **Auto-migrate only in Development**: Best of both worlds — fast dev iteration, safe production deploys.

## Decision

Wrapped `db.Database.Migrate()` in an `if (app.Environment.IsDevelopment())` guard. Production deployments require **explicit manual migration** (via CLI or deploy script).

## Consequences

- ✅ Production database schema changes are intentional and reviewable.
- ✅ Development still benefits from zero-friction auto-migration.
- ⚠️ Deployments with schema changes require an extra manual step (`dotnet ef database update`).
- ⚠️ Risk of forgetting to run migrations on deploy — mitigated by CI/CD scripts.

## References

- [`server/Heartbeat.Server/Program.cs`](../../server/Heartbeat.Server/Program.cs) — conditional migration guard
