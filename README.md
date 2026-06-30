# Heartbeat

Personal Windows PC app usage monitor.
https://heartbeat.shenxianovo.com

## Architecture

```mermaid
graph TB
    subgraph Desktop["Desktop (Windows)"]
        Runner["Heartbeat.Agent.Runner<br/><i>Console Host</i>"]
        WPF["Heartbeat.WPF<br/><i>WPF GUI Host</i>"]

        subgraph Agent["Heartbeat.Agent (Library)"]
            Monitor["AppMonitorService<br/><i>WinEvent Hook</i>"]
            Workers["UsageUpload / StatusUpload<br/>IconUpload Workers"]
            Cache["LocalCache<br/><i>JSON file</i>"]
            Helpers["ActiveWindowHelper<br/>IconHelper<br/><i>P/Invoke</i>"]
        end

        Runner --> Agent
        WPF --> Agent
    end

    subgraph Server["Server (Linux)"]
        subgraph API["Heartbeat.Server (ASP.NET Core)"]
            Controllers["Controllers<br/><i>Usage / App / Device / Report</i>"]
            Services["UsageService / ReportService<br/><i>merge + aggregate</i>"]
            Auth["ApiKey AuthenticationHandler"]
            DB["EF Core + PostgreSQL"]
        end
        Controllers --> Services
        Controllers --> Auth
        Services --> DB
    end

    subgraph Web["Frontend (Vue 3 + Vite)"]
        Client["NSwag Generated API Client"]
        UI["Timeline / Ranking / Weekly Chart"]
        Client --> UI
    end

    subgraph Shared["Heartbeat.Core"]
        DTOs["DTOs + UsageMerger"]
    end

    Agent -- "HTTP<br/>ApiKey auth" --> API
    Web -- "OpenAPI" --> API
    Agent -.-> Shared
    API -.-> Shared
```

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core (.NET 10), EF Core, PostgreSQL |
| Desktop Agent | .NET 10 (Windows), Generic Host, WinEvent Hooks (P/Invoke) |
| Desktop GUI | WPF (.NET 10) |
| Frontend | Vue 3, TypeScript, Vite |
| API Client | Auto-generated via OpenAPI / NSwag |
| Shared | Heartbeat.Core (.NET Class Library) |
| CI/CD | GitHub Actions |
| Deployment | Linux systemd service + static frontend hosting |

## Project Structure

```
Heartbeat
├─ desktop
│  ├─ Heartbeat.Agent/          # Monitoring & upload library     .NET Class Library
│  ├─ Heartbeat.Agent.Runner/   # Console host                   .NET Console
│  └─ Heartbeat.WPF/            # GUI host                       WPF
├─ server
│  └─ Heartbeat.Server/         # REST API server                ASP.NET Core
├─ frontend/                    # Dashboard web app              Vue 3 + Vite
├─ shared
│  └─ Heartbeat.Core/           # Shared DTOs & utilities        .NET Class Library
├─ deploy/                      # Deployment scripts & systemd
└─ docs/                        # Documentation
   ├─ adr/                      # Architecture Decision Records
   ├─ api.md                    # API documentation
   └─ db.md                     # Database ER diagram
```

## Architecture Decision Records (ADR)

See [`docs/adr/`](./docs/adr/) for all architecture decisions ([template](./docs/adr/adr-template.md)).

## Documentation

- [API Design](./docs/api.md)
- [Database Design](./docs/db.md)