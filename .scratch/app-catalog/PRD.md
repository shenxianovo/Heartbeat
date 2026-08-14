# Server-maintained App Catalog

Status: ready-for-agent

## Problem Statement

Heartbeat 已将 `App` 定义为跨平台产品、将 `AppIdentity` 定义为平台观测身份，但当前只有数据模型和管理员 merge API，没有负责已知产品映射的权威目录与可发现的管理流程。未知 macOS identity 会按设计创建 provisional App；因此同一 Chrome 当前分别落在 `win:chrome → App chrome` 与 `mac:com.google.chrome → App google.chrome`，Dashboard 诚实地显示为两个产品。

现状不是前端重复渲染，而是映射事实缺失。仅靠隐藏的 merge API 也不能形成产品保证：本地部署默认没有管理员 subject 配置，已知产品首次从新平台出现时不会自动归并，管理员决定与当前 `AppIdentity.AppId` 无法区分，服务端升级也不知道哪些本地决定必须保留。

## Outcome

服务端随版本发布一个严格校验、版本化的 App Catalog。启动时，统一的 App Catalog Reconciler 把内置 Catalog 与部署本地 Override 合成为生效映射，并通过事务化领域操作协调 App、AppIdentity、历史段、图标、权威知识、派生缓存和审计记录。

Dashboard 设置页向 Deployment Administrator 提供 provisional App 分类、dry-run、归并、创建产品、删除 Override、最近审计与 Catalog 候选 JSON 导出。普通数据视图继续按 App 聚合；未知产品不丢数据，只有管理员看到待归类状态。

本规格实现 [ADR-038](../../docs/adr/038-server-maintained-app-catalog.md)，术语以 [`server/CONTEXT.md`](../../server/CONTEXT.md) 为准。

## User Stories

1. As a Heartbeat owner, I want Windows and macOS identities for the same known product to aggregate automatically, so that Chrome and VS Code do not split in reports or timelines.
2. As a Heartbeat owner, I want raw AppIdentity facts preserved, so that product aggregation never erases what each platform actually observed.
3. As a Heartbeat owner, I want unknown identities retained as provisional Apps, so that missing Catalog knowledge never loses activity.
4. As a Deployment Administrator, I want provisional Apps visible in Settings, so that classification work is discoverable.
5. As a Deployment Administrator, I want to preview every mapping change, so that icons, knowledge, current activity and historical products are not changed blindly.
6. As a Deployment Administrator, I want to map an identity to an existing or newly created App, so that private and deployment-specific products can be classified.
7. As a Deployment Administrator, I want local decisions to override the built-in Catalog, so that a server update cannot silently undo my classification.
8. As a Deployment Administrator, I want deleting an Override to restore the built-in mapping or a provisional App, so that removal has explicit semantics.
9. As a Deployment Administrator, I want recent mapping and reconciliation history, so that global changes remain auditable.
10. As a maintainer, I want selected local mappings exported as a clean Catalog candidate JSON, so that verified production knowledge can be reviewed and committed.
11. As a maintainer, I want Catalog application deterministic and idempotent across backend replicas, so that concurrent startup cannot half-apply a mapping.
12. As a release operator, I want invalid Catalogs or failed reconciliation to stop startup, so that the service cannot continue creating knowingly split product data.
13. As a release operator, I want an older backend to preserve a newer already-applied Catalog during rollback, so that rollback does not reverse product history.
14. As an ordinary user, I want provisional classification state hidden, so that deployment administration does not leak into normal product views.
15. As a future operator of many deployments, I want the Catalog artifact separable from reconciliation semantics, so that distribution may later move to a signed snapshot service without redesigning App identity.

## Product Identity Contract

- `App` is the product aggregation dimension. Its stable Key is authoritative knowledge identity; DisplayName is mutable presentation text.
- `AppIdentity` is immutable platform evidence. Windows uses `win:`, macOS uses `mac:`, and synthetic identities use `sys:`.
- One App has one or more AppIdentity values. A single-platform known product uses the same model and is not provisional merely because it has one identity.
- Independently installable channels or variants are separate Apps by default: Chrome vs Chrome Beta, VS Code vs VS Code Insiders.
- Catalog entries are accepted only from real platform observation or supplier documentation. Name, icon or vendor similarity alone never creates a mapping.
- Published App Keys and identity mappings are append-only by default. Corrections use an explicit migration through the same reconciliation domain operation.
- Catalog owns product identity and canonical name only. Report visibility, noise filtering and statistics policy remain separate concerns.
- Catalog carries no product icons. Existing per-Owner/App first-valid icon behavior remains authoritative.

## Catalog Artifact

The built-in source of truth is `server/Heartbeat.Server/AppCatalog/app-catalog.json`.

- `schemaVersion` changes only when the JSON shape changes.
- `catalogVersion` is a monotonically increasing content snapshot version.
- Products contain canonical Key, DisplayName and a deterministically sorted identity list.
- Build and startup validation reject duplicate product Keys, duplicate identities, invalid identity prefixes/normalization, empty products, unknown schema versions and non-deterministic content.
- A canonical serialization hash detects content drift when `catalogVersion` is unchanged.
- The database records the last successfully applied catalogVersion and hash; it is an application record, not the version source of truth.
- If the database has a newer applied version than the binary, startup enters rollback compatibility mode: retain the database mapping, skip downgrade reconciliation and emit a clear warning.

## Reconciliation

- All mapping changes flow through one App Catalog Reconciler, whether the input is built-in Catalog or WebUI Override.
- Reconciliation runs during backend startup before requests are served and holds a PostgreSQL advisory transaction lock. Multiple replicas serialize and converge on one result.
- Local Override wins over built-in Catalog; absent both, the identity remains mapped one-to-one to a provisional App.
- AppIdentity resolution for ingest, presence and icon upload reads the same effective snapshot, so a Catalog-known identity first observed after startup binds directly to the canonical App. Catalog entries do not require pre-creating unobserved AppIdentity rows.
- The Reconciler generalizes the existing `AppMergeService` transaction: canonical metadata, AppIdentity rebinding, legacy ActivitySegment.AppId compatibility, Device current activity, icons, Strand Matchers, Muted Matchers, Recurrence Probes, derived question caches and merge receipts remain consistent.
- Catalog application automatically repairs existing provisional history and preserves an established formal App Id when possible.
- The same Catalog and database state are idempotent. Retries do not create additional Apps, duplicate receipts or repeat knowledge mutation.
- Invalid built-in content or a reconciliation transaction failure prevents backend startup. A built-in entry shadowed by an active local Override is skipped with an observable warning, not treated as failure.

## Local Override

- App Catalog Override is explicit persisted administrator intent, distinct from the effective `AppIdentity.AppId` result.
- One active Override maps one AppIdentity to one target App and records actor and timestamps. An AppIdentity has at most one active Override.
- Mapping to a new product creates the App and Override in the same transaction.
- Deleting an Override immediately reconciles the identity: use the built-in Catalog mapping when present; otherwise create or restore an independent provisional App.
- When a newly deployed Catalog contains the same mapping as an Override, the Override becomes promoted/inactive while its full audit history remains.
- Every Override mutation and Catalog reconciliation writes an append-only audit entry with version/hash where applicable, actor, timestamp and change summary. Audit records are not automatically deleted.

## Administration API

- Deployment Administrator remains a JWT `sub` configured through `Administration:Subjects`; username is never an authorization key.
- Auth platform owns discovery/display of immutable `sub`. Heartbeat does not add an account-identity management surface.
- `/api/v1/me` returns `isAdmin` for authenticated Dashboard visibility decisions.
- Every admin endpoint enforces authorization server-side even when the UI hides its entry.
- Typed endpoints provide:
  - provisional and classified product/identity inventory with relevant usage counts;
  - current Override and built-in source state;
  - dry-run classification to an existing or new App;
  - commit/update/delete Override;
  - recent reconciliation and Override audit;
  - deterministic Catalog candidate export.
- No WebUI or API endpoint imports/replaces the built-in Catalog JSON.

## Dashboard Settings

- `/settings` shows an App Catalog entry only when `/me.isAdmin` is true.
- The administrator page lists provisional Apps, their raw identities and enough usage context to classify them without opening the database.
- An administrator can choose an existing target App or enter a new canonical Key and DisplayName, inspect dry-run impact, then confirm.
- Active Overrides can be inspected, changed and deleted with the fallback behavior stated above.
- Recent audit entries show what changed and whether a local Override has been promoted into the built-in Catalog.
- Provisional Apps continue to appear normally in Report, Timeline and details. Administrators additionally see a subtle pending-classification marker; non-admin users do not receive internal classification state.

## Catalog Candidate Export

- The administrator explicitly selects which local mappings are suitable for upstreaming. Deployment-private Overrides are not selected by default.
- Export merges selected mappings with the current built-in snapshot and emits the complete next candidate, not a database dump or patch fragment.
- Output contains only schemaVersion, proposed catalogVersion, canonical product Key, DisplayName and identity lists.
- Output excludes numeric database Ids, Owner data, usage counts, administrator sub, timestamps, icons and audit records.
- Proposed version is current formal catalogVersion + 1. Repeated exports before that version is deployed keep the same proposed version and deterministically sorted bytes.
- Export is unavailable when selected mappings produce no content change.
- Successful deployment and reconciliation of the higher-version JSON is the only event that advances the database applied version.

## Initial Catalog

The first non-empty Catalog must include only independently verified identities. At minimum, integration fixtures and the local acceptance dataset cover:

- Google Chrome: `win:chrome` + `mac:com.google.chrome` → `chrome`, `Google Chrome`.
- Visual Studio Code: `win:code` + `mac:com.microsoft.vscode` → `vscode`, `Visual Studio Code`.
- QQ: `win:qq` + `mac:com.tencent.qq` → `qq`, `QQ`.
- Feishu: `win:feishu` + `mac:com.electron.lark` → `feishu`, canonical vendor display name confirmed during implementation.
- Finder: `mac:com.apple.finder` → `finder`, `Finder`.
- Heartbeat desktop identities observed from the Windows and macOS platform heads → one `heartbeat` product; exact shipped and legacy executable identities must be verified from the repository and real data before inclusion.

Ambiguous identities such as `mac:com.openai.codex` and `mac:com.electron.lark.iron` remain provisional until independently resolved.

## Testing Decisions

- Use the existing PostgreSQL integration fixture for schema, reconciliation, merge/knowledge migration, Override lifecycle, audit, rollback mode and advisory-lock concurrency.
- Catalog parser/hash tests are deterministic pure tests and include malformed, duplicate, non-normalized and unknown-version fixtures.
- A built-in mapping test begins with two provisional products and proves startup reconciliation yields one App across Report, segments, current activity, icon selection and App-key knowledge while retaining both raw identities.
- Override tests cover local precedence, mapping to existing/new App, update, delete-to-Catalog, delete-to-provisional, promotion by a later Catalog and audit retention.
- Authorization tests prove `isAdmin`, admin API denial for ordinary users and no mutation on rejected requests.
- Export tests compare exact bytes, stable ordering, privacy exclusions, repeated proposed version and no-change behavior.
- Dashboard tests cover admin navigation visibility, provisional inventory, dry-run confirmation, error recovery, audit display, export download and non-admin absence.
- Regenerate `frontend/src/api/client.ts` from the Development OpenAPI endpoint and run Vue type-check/tests after server DTO changes.
- Local end-to-end acceptance must make the existing Chrome split assertion green: `win:chrome` and `mac:com.google.chrome` resolve to one App product while the identities remain queryable.

## Rollout and Operations

- Keep the existing `ADMIN_SUBJECT` compose wiring; production operators fill it with Auth platform `sub` before using the management page.
- The first Catalog deployment runs normal startup reconciliation against existing data; operators take a database backup and inspect reconciliation logs/receipts.
- Catalog content updates enter through code review and backend deployment only. WebUI export produces candidates; it never hot-replaces the built-in artifact.
- A future Catalog Distribution Service may replace artifact transport only. Heartbeat retains schema validation, cached last-good snapshot, Reconciler, local Override and audit semantics; service availability must not depend on real-time distribution connectivity.

## Out of Scope

- Heuristic product matching by names, icons, vendors, paths or fuzzy similarity.
- Per-Owner App identity mappings.
- Catalog-managed icons, report hiding, noise classification or statistics rules.
- WebUI import of Catalog JSON.
- Granting/revoking Deployment Administrator from Heartbeat UI.
- Resolving ambiguous observed identities without independent evidence.
- Replacing the Auth platform or exposing JWT `sub` from Heartbeat account settings.
- Building an external Catalog Distribution Service in this feature.

## Delivery Graph

```text
01 Catalog artifact/state
  └─ 02 Built-in reconciliation
       └─ 03 Override lifecycle
            ├─ 04 Admin API
            │    └─ 05 Settings UI
            │         └─ 06 Candidate export
            └─ 07 Initial verified Catalog

06 + 07 ──▶ 08 End-to-end acceptance and operations
```

Every issue is implementation-ready, declares its blocking edge and leaves the repository in a runnable, testable state.
