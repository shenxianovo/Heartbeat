# Graph Report - .  (2026-05-28)

## Corpus Check
- Corpus is ~29,708 words - fits in a single context window. You may not need a graph.

## Summary
- 1106 nodes · 1403 edges · 110 communities (78 shown, 32 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 16 edges (avg confidence: 0.86)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Database Migrations|Database Migrations]]
- [[_COMMUNITY_Server Controllers|Server Controllers]]
- [[_COMMUNITY_Agent HTTP Auth|Agent HTTP Auth]]
- [[_COMMUNITY_Icon & Win32 Helpers|Icon & Win32 Helpers]]
- [[_COMMUNITY_Project Dependencies|Project Dependencies]]
- [[_COMMUNITY_Test Fixtures & Guard|Test Fixtures & Guard]]
- [[_COMMUNITY_WPF Application Shell|WPF Application Shell]]
- [[_COMMUNITY_Architecture Decisions|Architecture Decisions]]
- [[_COMMUNITY_Server NuGet Packages|Server NuGet Packages]]
- [[_COMMUNITY_Frontend API Client Functions|Frontend API Client Functions]]
- [[_COMMUNITY_WPF Logging Ring Buffer|WPF Logging Ring Buffer]]
- [[_COMMUNITY_TypeScript API Client|TypeScript API Client]]
- [[_COMMUNITY_Active Window Tracking|Active Window Tracking]]
- [[_COMMUNITY_API Client DTOs|API Client DTOs]]
- [[_COMMUNITY_WPF Main ViewModel|WPF Main ViewModel]]
- [[_COMMUNITY_Frontend TS App Config|Frontend TS App Config]]
- [[_COMMUNITY_App Monitor Service|App Monitor Service]]
- [[_COMMUNITY_App Controller|App Controller]]
- [[_COMMUNITY_Frontend Package Config|Frontend Package Config]]
- [[_COMMUNITY_Frontend TS Node Config|Frontend TS Node Config]]
- [[_COMMUNITY_Usage Merger Tests|Usage Merger Tests]]
- [[_COMMUNITY_Domain Context Map|Domain Context Map]]
- [[_COMMUNITY_Frontend Routing & Auth|Frontend Routing & Auth]]
- [[_COMMUNITY_Config Manager|Config Manager]]
- [[_COMMUNITY_Validation Policy Tests|Validation Policy Tests]]
- [[_COMMUNITY_Background Workers|Background Workers]]
- [[_COMMUNITY_API Client Models|API Client Models]]
- [[_COMMUNITY_Update Service|Update Service]]
- [[_COMMUNITY_Dashboard Components|Dashboard Components]]
- [[_COMMUNITY_Report Service|Report Service]]
- [[_COMMUNITY_Device Service|Device Service]]
- [[_COMMUNITY_Public User Controller|Public User Controller]]
- [[_COMMUNITY_Heartbeat API Client|Heartbeat API Client]]
- [[_COMMUNITY_Local Cache Storage|Local Cache Storage]]
- [[_COMMUNITY_App Service|App Service]]
- [[_COMMUNITY_Device Service Tests|Device Service Tests]]
- [[_COMMUNITY_Usage Service|Usage Service]]
- [[_COMMUNITY_DateRange Tests|DateRange Tests]]
- [[_COMMUNITY_Server Launch Settings|Server Launch Settings]]
- [[_COMMUNITY_Agent Launch Settings|Agent Launch Settings]]
- [[_COMMUNITY_Usage Upload Service|Usage Upload Service]]
- [[_COMMUNITY_User Service|User Service]]
- [[_COMMUNITY_Bearer Token Handler|Bearer Token Handler]]
- [[_COMMUNITY_Auth Service Client|Auth Service Client]]
- [[_COMMUNITY_Usage Validation Policy|Usage Validation Policy]]
- [[_COMMUNITY_WPF Main Window|WPF Main Window]]
- [[_COMMUNITY_Usage Merger Core|Usage Merger Core]]
- [[_COMMUNITY_Agent Host Extensions|Agent Host Extensions]]
- [[_COMMUNITY_Registry Auto Start|Registry Auto Start]]
- [[_COMMUNITY_AppDbContext|AppDbContext]]
- [[_COMMUNITY_Icon Upload Service|Icon Upload Service]]
- [[_COMMUNITY_Server App Settings|Server App Settings]]
- [[_COMMUNITY_DB Context Snapshot|DB Context Snapshot]]
- [[_COMMUNITY_Current User Service|Current User Service]]
- [[_COMMUNITY_AppDuration DTO|AppDuration DTO]]
- [[_COMMUNITY_AppInfo DTO|AppInfo DTO]]
- [[_COMMUNITY_AppUsageItem DTO|AppUsageItem DTO]]
- [[_COMMUNITY_AppUsageResponse DTO|AppUsageResponse DTO]]
- [[_COMMUNITY_DeviceInfo DTO|DeviceInfo DTO]]
- [[_COMMUNITY_DeviceStatusRequest DTO|DeviceStatusRequest DTO]]
- [[_COMMUNITY_DeviceStatusResponse DTO|DeviceStatusResponse DTO]]
- [[_COMMUNITY_Status Upload Service|Status Upload Service]]
- [[_COMMUNITY_Activity Timeline Component|Activity Timeline Component]]
- [[_COMMUNITY_Timeline Drag Composable|Timeline Drag Composable]]
- [[_COMMUNITY_DateRange Core|DateRange Core]]
- [[_COMMUNITY_Initial Create Migration|Initial Create Migration]]
- [[_COMMUNITY_Composite Index Migration|Composite Index Migration]]
- [[_COMMUNITY_Refactor IDs Migration|Refactor IDs Migration]]
- [[_COMMUNITY_App Icons Migration|App Icons Migration]]
- [[_COMMUNITY_Status Fields Migration|Status Fields Migration]]
- [[_COMMUNITY_Remove ApiKey Migration|Remove ApiKey Migration]]
- [[_COMMUNITY_Device Owner Migration|Device Owner Migration]]
- [[_COMMUNITY_User Table Migration|User Table Migration]]
- [[_COMMUNITY_Auto Start Interface|Auto Start Interface]]
- [[_COMMUNITY_Machine Identity|Machine Identity]]
- [[_COMMUNITY_App Labels|App Labels]]
- [[_COMMUNITY_WPF Program Entry|WPF Program Entry]]
- [[_COMMUNITY_Daily Report DTO|Daily Report DTO]]
- [[_COMMUNITY_Usage Upload DTO|Usage Upload DTO]]
- [[_COMMUNITY_AppInfo Response DTO|AppInfo Response DTO]]
- [[_COMMUNITY_Agent Config Model|Agent Config Model]]
- [[_COMMUNITY_Device Info DTO|Device Info DTO]]
- [[_COMMUNITY_Device Status DTO|Device Status DTO]]
- [[_COMMUNITY_App Entity|App Entity]]
- [[_COMMUNITY_TS Config Root|TS Config Root]]
- [[_COMMUNITY_AppIcon Entity|AppIcon Entity]]
- [[_COMMUNITY_AppUsage Entity|AppUsage Entity]]
- [[_COMMUNITY_Device Entity|Device Entity]]
- [[_COMMUNITY_User Entity|User Entity]]
- [[_COMMUNITY_AppUsage Response DTO|AppUsage Response DTO]]
- [[_COMMUNITY_Icon Upload DTO|Icon Upload DTO]]
- [[_COMMUNITY_Device Status Request DTO|Device Status Request DTO]]
- [[_COMMUNITY_Weekly Report DTO|Weekly Report DTO]]
- [[_COMMUNITY_Community 93|Community 93]]
- [[_COMMUNITY_Community 94|Community 94]]
- [[_COMMUNITY_Community 107|Community 107]]
- [[_COMMUNITY_Community 108|Community 108]]
- [[_COMMUNITY_Community 109|Community 109]]

## God Nodes (most connected - your core abstractions)
1. `IconHelper` - 35 edges
2. `App` - 23 edges
3. `Client` - 23 edges
4. `ActiveWindowHelper` - 18 edges
5. `IntPtr` - 18 edges
6. `MainViewModel` - 17 edges
7. `compilerOptions` - 17 edges
8. `DllImport` - 15 edges
9. `RingBufferSink` - 15 edges
10. `compilerOptions` - 15 edges

## Surprising Connections (you probably didn't know these)
- `Heartbeat.Agent.Runner (Console Host)` --implements--> `Collection Context (desktop/)`  [INFERRED]
  desktop/Heartbeat.Agent.Runner/Properties/launchSettings.json → CONTEXT-MAP.md
- `Heartbeat.Server (ASP.NET Core API)` --implements--> `Analytics Context (server/)`  [INFERRED]
  server/Heartbeat.Server/appsettings.json → CONTEXT-MAP.md
- `heartbeat-web (Vue 3 + Vite Frontend)` --implements--> `Dashboard Context (frontend/)`  [INFERRED]
  frontend/package.json → CONTEXT-MAP.md
- `ApiKey Header Authentication` --rationale_for--> `Heartbeat.Agent.Runner (Console Host)`  [INFERRED]
  README.md → desktop/Heartbeat.Agent.Runner/Properties/launchSettings.json
- `Generic Host Lifecycle for Desktop Client` --rationale_for--> `Heartbeat.Agent.Runner (Console Host)`  [EXTRACTED]
  README.md → desktop/Heartbeat.Agent.Runner/Properties/launchSettings.json

## Hyperedges (group relationships)
- **Dashboard Component Composition Tree** — dashboard_component, activity_timeline, current_app_panel, status_cards, today_ranking, weekly_chart [EXTRACTED 1.00]
- **Collection-Analytics-Dashboard Data Pipeline** — collection_context, analytics_context, dashboard_context, shared_kernel [EXTRACTED 1.00]
- **CI/CD Deployment Pipeline** — deploy_backend_workflow, deploy_frontend_workflow, heartbeat_server, frontend_app [EXTRACTED 1.00]
- **Velopack Release Pipeline (CI + Install + Update Source)** — release_desktop_workflow, adr009_velopack_auto_update, adr010_per_user_localappdata, adr011_github_releases_update_source, concept_velopack [EXTRACTED 1.00]
- **Usage Data Integrity (Merging + Cache + Upload)** — adr001_server_side_usage_merging, adr008_local_cache_offline_retry, concept_usage_merger, concept_app_usage [EXTRACTED 0.95]
- **Agent Architecture (Host + Library + Tracking + Auth)** — adr002_event_driven_window_tracking, adr003_generic_host_lifecycle, adr004_apikey_header_auth, adr005_extract_agent_library, concept_agent [INFERRED 0.85]

## Communities (110 total, 32 thin omitted)

### Community 0 - "Database Migrations"
Cohesion: 0.05
Nodes (25): Migration, Heartbeat.Server.Migrations, InitialCreate, AddCompositeIndex, Heartbeat.Server.Migrations, AddAppIcons, Heartbeat.Server.Migrations, AddStatusFields (+17 more)

### Community 1 - "Server Controllers"
Cohesion: 0.06
Nodes (34): ControllerBase, DeviceController, Heartbeat.Server.Controllers, Heartbeat.Server.Controllers, ReportController, Heartbeat.Server.Controllers, UsageController, ReportService (+26 more)

### Community 2 - "Agent HTTP Auth"
Cohesion: 0.06
Nodes (27): AuthServiceClient, CancellationToken, Task, CancellationToken, ConfigManager, DateTimeOffset, string, Task (+19 more)

### Community 3 - "Icon & Win32 Helpers"
Cohesion: 0.12
Nodes (10): DllImport, int, IntPtr, uint, EnumWindowsProc, MarshalAs, SHFILEINFO, StringBuilder (+2 more)

### Community 4 - "Project Dependencies"
Cohesion: 0.06
Nodes (30): net10.0-windows, Serilog (4.3.1), Serilog.Extensions.Hosting (10.0.0), Serilog.Sinks.Console (6.1.1), Serilog.Sinks.File (7.0.0), Microsoft.NET.Sdk, net10.0-windows, Serilog (4.3.1) (+22 more)

### Community 5 - "Test Fixtures & Guard"
Cohesion: 0.10
Nodes (17): string, SqliteFixture, TestDbContext, IDisposable, ModelConfigurationBuilder, Mutex, AppDbContext, AppUsageItem (+9 more)

### Community 6 - "WPF Application Shell"
Cohesion: 0.09
Nodes (16): Application, ContextMenu, DllImport, IntPtr, SingleInstanceGuard, Task, uint, ExitEventArgs (+8 more)

### Community 7 - "Architecture Decisions"
Cohesion: 0.11
Nodes (27): ADR-001: Server-Side Usage Merging, ADR-002: Event-Driven Window Tracking, ADR-003: Generic Host Lifecycle, ADR-004: ApiKey Header Authentication, ADR-005: Extract Agent Library, ADR-006: Dedicated Report Endpoints, ADR-007: Disable Prod Auto-Migration, ADR-008: Local Cache Offline Retry (+19 more)

### Community 8 - "Server NuGet Packages"
Cohesion: 0.08
Nodes (22): Microsoft.AspNetCore.Authentication.JwtBearer (10.0.7), Microsoft.AspNetCore.OpenApi (10.0.3), Microsoft.EntityFrameworkCore (10.0.3), Microsoft.EntityFrameworkCore.Design (10.0.3), Microsoft.EntityFrameworkCore.Sqlite (10.0.8), Npgsql.EntityFrameworkCore.PostgreSQL (10.0.0), Microsoft.NET.Sdk.Web, net10.0 (+14 more)

### Community 9 - "Frontend API Client Functions"
Cohesion: 0.14
Nodes (15): AppSummary, authHttp, client, fetchDailyReport(), fetchPublicApps(), fetchPublicDailyReport(), fetchPublicDevices(), fetchPublicDeviceStatus() (+7 more)

### Community 10 - "WPF Logging Ring Buffer"
Cohesion: 0.11
Nodes (16): DateTime, bool, int, IReadOnlyList, long, string, ILogEventSink, Lock (+8 more)

### Community 12 - "Active Window Tracking"
Cohesion: 0.23
Nodes (7): DllImport, IntPtr, uint, MSG, ActiveWindowHelper, Heartbeat.Agent.Utils, WinEventDelegate

### Community 13 - "API Client DTOs"
Cohesion: 0.10
Nodes (13): ApiException, IAppDurationItem, IAppInfoResponse, IAppUsageItem, IAppUsageResponse, IconUploadRequest, IDailyReportResponse, IDeviceInfoResponse (+5 more)

### Community 14 - "WPF Main ViewModel"
Cohesion: 0.11
Nodes (11): AppMonitorService, bool, ConfigManager, IAutoStartService, int, IReadOnlyList, string, ObservableObject (+3 more)

### Community 15 - "Frontend TS App Config"
Cohesion: 0.11
Nodes (18): compilerOptions, allowImportingTsExtensions, isolatedModules, jsx, lib, module, moduleDetection, moduleResolution (+10 more)

### Community 16 - "App Monitor Service"
Cohesion: 0.13
Nodes (11): AppUsageItem, CancellationToken, DateTimeOffset, List, object, string, Task, IHostedService (+3 more)

### Community 17 - "App Controller"
Cohesion: 0.15
Nodes (13): AllowAnonymous, AppService, Authorize, AppController, Heartbeat.Server.Controllers, AppInfoResponse, HttpGet, HttpPost (+5 more)

### Community 18 - "Frontend Package Config"
Cohesion: 0.12
Nodes (16): dependencies, vue, vue-router, devDependencies, typescript, vite, @vitejs/plugin-vue, vue-tsc (+8 more)

### Community 19 - "Frontend TS Node Config"
Cohesion: 0.12
Nodes (16): compilerOptions, allowImportingTsExtensions, isolatedModules, lib, module, moduleDetection, moduleResolution, noEmit (+8 more)

### Community 20 - "Usage Merger Tests"
Cohesion: 0.26
Nodes (4): UsageMergerTests, AppUsageItem, DateTimeOffset, Fact

### Community 21 - "Domain Context Map"
Cohesion: 0.18
Nodes (17): Heartbeat.Agent.Runner (Console Host), Analytics Context (server/), ApiKey Header Authentication, Collection Context (desktop/), Context Map (Collection / Analytics / Dashboard), Dashboard Context (frontend/), Deploy Backend (GitHub Actions), Deploy Frontend (GitHub Actions) (+9 more)

### Community 22 - "Frontend Routing & Auth"
Cohesion: 0.15
Nodes (12): RESERVED_ROUTES, router, authStore, clearAuth(), handleCallback(), logout(), redirectToLogin(), refreshToken (+4 more)

### Community 23 - "Config Manager"
Cohesion: 0.26
Nodes (7): Action, ConfigManager, Heartbeat.Agent.Configuration, AgentConfig, object, string, JsonSerializerOptions

### Community 24 - "Validation Policy Tests"
Cohesion: 0.29
Nodes (4): UsageValidationPolicyTests, AppUsageItem, DateTimeOffset, Fact

### Community 25 - "Background Workers"
Cohesion: 0.18
Nodes (9): BackgroundService, CancellationToken, Task, CancellationToken, Task, Heartbeat.Agent.Workers, StatusUploadWorker, Heartbeat.Agent.Workers (+1 more)

### Community 26 - "API Client Models"
Cohesion: 0.18
Nodes (3): DailyReportResponse, UsageUploadRequest, WeeklyReportResponse

### Community 27 - "Update Service"
Cohesion: 0.15
Nodes (8): bool, string, Task, TimeSpan, UpdateService, Timer, UpdateInfo, UpdateManager

### Community 28 - "Dashboard Components"
Cohesion: 0.24
Nodes (5): API module (getIconUrl, AppUsageResponse), appLabels module (getAppLabel), authStore (auth state), useHeartbeat composable, useTimelineDrag composable

### Community 29 - "Report Service"
Cohesion: 0.20
Nodes (10): AppDurationItem, DailyReportResponse, AppDbContext, DateRange, DateTimeOffset, List, Task, Heartbeat.Server.Services (+2 more)

### Community 30 - "Device Service"
Cohesion: 0.19
Nodes (9): Device, DeviceStatusResponse, AppDbContext, DeviceInfoResponse, List, string, Task, DeviceService (+1 more)

### Community 31 - "Public User Controller"
Cohesion: 0.37
Nodes (6): Heartbeat.Server.Controllers, PublicUserController, DateTimeOffset, HttpGet, IActionResult, Task

### Community 32 - "Heartbeat API Client"
Cohesion: 0.27
Nodes (8): CancellationToken, DeviceStatusRequest, HttpResponseMessage, IconUploadRequest, Task, UsageUploadRequest, Heartbeat.Agent.Http, HeartbeatApiClient

### Community 33 - "Local Cache Storage"
Cohesion: 0.24
Nodes (7): AppUsageItem, int, List, string, ReaderWriterLockSlim, Heartbeat.Agent.Storage, LocalCache

### Community 34 - "App Service"
Cohesion: 0.27
Nodes (6): AppDbContext, AppInfoResponse, List, Task, AppService, Heartbeat.Server.Services

### Community 35 - "Device Service Tests"
Cohesion: 0.29
Nodes (5): Fact, SqliteFixture, Task, DeviceServiceTests, Heartbeat.Server.Tests.Services

### Community 36 - "Usage Service"
Cohesion: 0.20
Nodes (8): AppUsageResponse, AppDbContext, DateTimeOffset, List, Task, UsageUploadRequest, Heartbeat.Server.Services, UsageService

### Community 38 - "Server Launch Settings"
Cohesion: 0.20
Nodes (9): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, http, profiles (+1 more)

### Community 39 - "Agent Launch Settings"
Cohesion: 0.22
Nodes (9): profiles, $schema, commandName, environmentVariables, DOTNET_ENVIRONMENT, commandName, environmentVariables, Development (+1 more)

### Community 40 - "Usage Upload Service"
Cohesion: 0.31
Nodes (6): AppUsageItem, List, Task, UsageUploadRequest, Heartbeat.Agent.Services, UsageUploadService

### Community 41 - "User Service"
Cohesion: 0.24
Nodes (7): AuthUserInfo, IHttpClientFactory, AppDbContext, Task, Heartbeat.Server.Services, UserService, User

### Community 42 - "Bearer Token Handler"
Cohesion: 0.22
Nodes (7): DelegatingHandler, CancellationToken, HttpRequestMessage, HttpResponseMessage, Task, BearerTokenHandler, Heartbeat.Agent.Http

### Community 43 - "Auth Service Client"
Cohesion: 0.29
Nodes (5): CancellationToken, HttpResponseMessage, Task, AuthServiceClient, Heartbeat.Agent.Http

### Community 44 - "Usage Validation Policy"
Cohesion: 0.25
Nodes (6): UsageValidationPolicy, AppUsageItem, DateTimeOffset, int, List, TimeSpan

### Community 45 - "WPF Main Window"
Cohesion: 0.25
Nodes (5): CancelEventArgs, Heartbeat.WPF, MainWindow, TextChangedEventArgs, Window

### Community 46 - "Usage Merger Core"
Cohesion: 0.29
Nodes (5): Heartbeat.Core, UsageMerger, AppUsageItem, List, TimeSpan

### Community 47 - "Agent Host Extensions"
Cohesion: 0.29
Nodes (5): ConfigManager, SingleInstanceGuard, AgentHostExtensions, Heartbeat.Agent.Hosting, IServiceCollection

### Community 48 - "Registry Auto Start"
Cohesion: 0.29
Nodes (4): string, IAutoStartService, Heartbeat.Agent.Services, RegistryAutoStartService

### Community 49 - "AppDbContext"
Cohesion: 0.33
Nodes (4): AppDbContext, Heartbeat.Server.Data, DbContext, ModelBuilder

### Community 50 - "Icon Upload Service"
Cohesion: 0.33
Nodes (4): Task, HashSet, Heartbeat.Agent.Services, IconUploadService

### Community 51 - "Server App Settings"
Cohesion: 0.33
Nodes (5): AllowedHosts, Logging, LogLevel, Default, Microsoft.AspNetCore

### Community 52 - "DB Context Snapshot"
Cohesion: 0.33
Nodes (4): AppDbContextModelSnapshot, Heartbeat.Server.Migrations, ModelSnapshot, ModelBuilder

### Community 53 - "Current User Service"
Cohesion: 0.40
Nodes (3): CurrentUserService, Heartbeat.Server.Services, ICurrentUserService

### Community 61 - "Status Upload Service"
Cohesion: 0.40
Nodes (3): Task, Heartbeat.Agent.Services, StatusUploadService

### Community 62 - "Activity Timeline Component"
Cohesion: 0.40
Nodes (3): [], endP, startP

### Community 64 - "DateRange Core"
Cohesion: 0.60
Nodes (4): Day(), Week(), DateRange, DateTimeOffset

### Community 65 - "Initial Create Migration"
Cohesion: 0.40
Nodes (3): Heartbeat.Server.Migrations, InitialCreate, ModelBuilder

### Community 66 - "Composite Index Migration"
Cohesion: 0.40
Nodes (3): AddCompositeIndex, Heartbeat.Server.Migrations, ModelBuilder

### Community 67 - "Refactor IDs Migration"
Cohesion: 0.40
Nodes (3): Heartbeat.Server.Migrations, RefactorAppAndDeviceIds, ModelBuilder

### Community 68 - "App Icons Migration"
Cohesion: 0.40
Nodes (3): AddAppIcons, Heartbeat.Server.Migrations, ModelBuilder

### Community 69 - "Status Fields Migration"
Cohesion: 0.40
Nodes (3): AddStatusFields, Heartbeat.Server.Migrations, ModelBuilder

### Community 70 - "Remove ApiKey Migration"
Cohesion: 0.40
Nodes (3): Heartbeat.Server.Migrations, RemoveDeviceApiKey, ModelBuilder

### Community 71 - "Device Owner Migration"
Cohesion: 0.40
Nodes (3): AddDeviceOwnerAndHardwareId, Heartbeat.Server.Migrations, ModelBuilder

### Community 72 - "User Table Migration"
Cohesion: 0.40
Nodes (3): AddUserTable, Heartbeat.Server.Migrations, ModelBuilder

### Community 74 - "Machine Identity"
Cohesion: 0.40
Nodes (3): Lazy, Heartbeat.Agent.Utils, MachineIdentity

### Community 77 - "Daily Report DTO"
Cohesion: 0.50
Nodes (3): AppDurationItem, DailyReportResponse, Heartbeat.Core.DTOs.Reports

### Community 78 - "Usage Upload DTO"
Cohesion: 0.50
Nodes (3): AppUsageItem, Heartbeat.Core.DTOs.Usage, UsageUploadRequest

## Knowledge Gaps
- **401 isolated node(s):** `net10.0-windows`, `Microsoft.Extensions.Hosting (10.0.3)`, `Microsoft.Extensions.Http (10.0.3)`, `Serilog (4.3.1)`, `Serilog.Extensions.Hosting (10.0.0)` (+396 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **32 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `BearerTokenHandlerTests` connect `Agent HTTP Auth` to `Test Fixtures & Guard`?**
  _High betweenness centrality (0.006) - this node is a cross-community bridge._
- **Why does `Client` connect `TypeScript API Client` to `Frontend API Client Functions`, `API Client DTOs`?**
  _High betweenness centrality (0.005) - this node is a cross-community bridge._
- **What connects `net10.0-windows`, `Microsoft.Extensions.Hosting (10.0.3)`, `Microsoft.Extensions.Http (10.0.3)` to the rest of the system?**
  _408 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Database Migrations` be split into smaller, more focused modules?**
  _Cohesion score 0.047619047619047616 - nodes in this community are weakly interconnected._
- **Should `Server Controllers` be split into smaller, more focused modules?**
  _Cohesion score 0.05708245243128964 - nodes in this community are weakly interconnected._
- **Should `Agent HTTP Auth` be split into smaller, more focused modules?**
  _Cohesion score 0.059233449477351915 - nodes in this community are weakly interconnected._
- **Should `Icon & Win32 Helpers` be split into smaller, more focused modules?**
  _Cohesion score 0.12311265969802555 - nodes in this community are weakly interconnected._