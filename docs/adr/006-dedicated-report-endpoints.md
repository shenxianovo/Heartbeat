# ADR-006: Redesign API with Dedicated Report Endpoints

## Status: Accepted（日/周报的单个 `DateTimeOffset date` 契约 amended by [ADR-044](./044-browser-civil-calendar-window.md)；通用 Usage instant range 保留）

## Date: 2026-03-05

[`04120cc`](https://github.com/shenxianovo/heartbeat/commit/04120cc) — feat: redesign API structure for usage and device status

## Context

The original API exposed raw usage records (`GET /usage?start=...&end=...`) and left all aggregation to the frontend. The frontend had to:

1. Fetch all raw records for the day, then group by app and sum durations.
2. Fetch 7 days of records for the weekly chart, then aggregate per day.

This meant the frontend was doing **heavy data processing** (sorting, grouping, summing) that could be hundreds of records on a busy day. It also coupled the frontend to the raw data schema — any change in how records were stored would break the frontend logic.

Alternatives:

1. **Keep raw-only API**: Frontend does all aggregation. Simple server, but pushes complexity to every client.
2. **Add server-side report endpoints**: `GET /report/daily`, `GET /report/weekly`. Server aggregates using SQL, returns pre-computed summaries.

## Decision

Added a **`ReportController`** (authenticated, route `api/v1/reports`) with dedicated endpoints:

- `GET /api/v1/reports/daily?deviceId=&date=` — returns per-app duration totals for one day.
- `GET /api/v1/reports/weekly?deviceId=&date=` — returns daily totals for a 7-day window.

The raw `GET /api/v1/usage` endpoint is preserved for detailed timeline views.

The same aggregation logic is also exposed **publicly by username** through `PublicUserController`
(route `api/v1/users/{username}`), e.g. `GET /api/v1/users/{username}/reports/daily`,
`/reports/weekly`, `/usage`, `/apps`, `/devices` — these are unauthenticated read endpoints
backing the public dashboard.

## Consequences

- ✅ Frontend receives ready-to-render data — no client-side aggregation needed.
- ✅ Server-side SQL aggregation is faster than transferring and processing raw records.
- ✅ Multiple clients (web, future mobile) get consistent report logic.
- ⚠️ Two query paths (raw + aggregated) that must stay in sync if the data model changes.
- ⚠️ Report endpoints add server-side complexity (`ReportService` with date/timezone handling).

## References

- [`server/Heartbeat.Server/Controllers/ReportController.cs`](../../server/Heartbeat.Server/Controllers/ReportController.cs) — authenticated report endpoints (`api/v1/reports`)
- [`server/Heartbeat.Server/Controllers/PublicUserController.cs`](../../server/Heartbeat.Server/Controllers/PublicUserController.cs) — public-by-username read endpoints (`api/v1/users/{username}`)
- [`server/Heartbeat.Server/Services/ReportService.cs`](../../server/Heartbeat.Server/Services/ReportService.cs) — aggregation logic
- [`shared/Heartbeat.Core/DTOs/Reports/DailyReportResponse.cs`](../../shared/Heartbeat.Core/DTOs/Reports/DailyReportResponse.cs) — daily report DTO
- [`shared/Heartbeat.Core/DTOs/Reports/WeeklyReportResponse.cs`](../../shared/Heartbeat.Core/DTOs/Reports/WeeklyReportResponse.cs) — weekly report DTO
