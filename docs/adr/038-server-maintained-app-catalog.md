# ADR-038: 服务端维护部署全局 App Catalog

## Status: Accepted

## Date: 2026-08-14

## Context

ADR-034 将 App 定义为跨平台产品、AppIdentity 定义为平台观测身份，但只提供人工归并无法保证 Chrome 等已知产品首次从新平台出现时立即聚合。现有数据库已经出现 Windows 与 macOS 身份落入两个 App 的情况；隐藏的管理 API 也不足以形成可执行的分类流程。

跨平台产品映射影响所有 Owner，既要保留平台观测事实，又不能使用名称、图标或厂商字符串进行启发式归并。部署还需要覆盖内置判断，但服务端升级不能静默冲掉管理员已经确认的本地决定。

## Decision

Heartbeat 由服务端维护部署全局 App Catalog，声明已知产品的规范 Key、DisplayName 与各平台 AppIdentity。Catalog 以仓库内 `server/Heartbeat.Server/AppCatalog/app-catalog.json` 为权威来源，包含文件格式 `schemaVersion`、递增的 `catalogVersion` 与产品数组，随服务端版本发布并在构建、启动时严格校验；数据库记录最近成功应用的版本与内容哈希，拒绝版本未变但内容漂移的目录。目录内身份确定映射，目录外身份创建 provisional App。Catalog 决定规范 Key 与 DisplayName，协调既有数据时尽量保留已经稳定使用的正式 App Id。

Catalog 产品可以声明一个或多个 AppIdentity：单平台稳定产品与跨平台产品使用同一模型，平台只由 identity 前缀表达。`provisional` 表示产品尚未被识别，而不是缺少跨平台版本；例如 Finder 可以作为只有 `mac:com.apple.finder` 的正式 App，未来若出现其他平台身份只需追加到同一产品。

App Key 发布后保持稳定，DisplayName 可以随产品品牌文案更新；确需修改 Key 时必须执行同步迁移权威知识的显式领域操作。Catalog 条目与 identity 映射默认只增不删，错误映射通过显式迁移到正确产品修正，不能通过版本更新静默拆散历史数据。可以独立安装并同时运行的发行渠道或产品变体默认建模为不同 App，例如 Chrome 与 Chrome Beta、VS Code 与 VS Code Insiders；部署管理员仍可通过 Override 主动合并。

内置 Catalog 只收录经过真实设备观察或供应商资料确认的平台原生身份；名称、图标、厂商字符串或当前数据库中的相似关系都不能单独作为收录证据。有歧义的身份继续保留 provisional，等待进一步确认。

服务端启动时由统一的 App Catalog Reconciler 计算生效映射并协调数据库。Reconciler 先获取 PostgreSQL advisory transaction lock，使多个 backend 实例串行、幂等地应用同一目录；随后复用并扩展现有事务化 merge 能力，在一个领域操作中完成规范产品信息、AppIdentity 重绑、历史 provisional App 归并、图标与权威知识迁移、派生缓存失效和 receipt 记录。Catalog 新增已知身份时自动修复历史数据，而不是只影响未来段。

内置 Catalog 自相矛盾或协调事务失败时，backend 拒绝启动并给出明确错误。部署本地覆盖以显式 App Catalog Override 数据保存在数据库并优先于内置 Catalog；每条 Override 把一个 AppIdentity 指向一个目标 App，并记录修改人与时间，不能从 AppIdentity 当前的 AppId 反向猜测。管理页创建新产品时，在同一事务中创建目标 App 与对应 Override。删除 Override 后立即重新协调：Catalog 有映射则恢复内置产品，没有映射则把 identity 拆回独立 provisional App。Catalog 更新遇到本地覆盖时跳过对应内置项并告警，不静默覆盖管理员决定。

Dashboard 的设置区域提供仅 Deployment Administrator 可见的 App Catalog 管理页，展示 provisional App、平台身份与使用量，并通过 dry-run 后执行归并或修正规范产品信息。管理员身份以 Auth 平台签发的不可变 JWT `sub` 为准：Auth 平台负责向部署者展示该标识，Heartbeat 的 `/me` 只投影 `isAdmin`。管理员名单由部署环境配置，修改后重启服务生效；产品 UI 不提供授予或撤销管理员权限的能力。

管理页允许管理员勾选值得沉淀到代码库的本地 Override，并导出完整、确定性排序的 Catalog 候选 JSON。候选文件只包含 schemaVersion、catalogVersion、产品 Key、DisplayName 与 AppIdentity，不包含数据库 Id、Owner、使用量、管理员 sub、时间或审计记录。候选版本恒为当前正式 Catalog 版本加一；正式版本部署前重复导出保持同一候选版本，可以累积多个映射。

Catalog 版本的权威来源始终是随 backend 发布的 JSON。WebUI 新建 Override 会立即经同一 Reconciler 生效并写审计，但不会增加 catalogVersion；导出候选文件也不会修改数据库的已应用版本。只有包含更高版本 JSON 的 backend 启动并成功完成协调后，数据库才记录新的已应用版本与内容哈希。没有内容变化时不生成新候选版本。

WebUI 不提供 Catalog JSON 导入；正式目录只能经过代码审查并随 backend 部署。新正式 Catalog 已包含某条本地 Override 的同一映射时，Reconciler 将该 Override 标记为已沉淀并停止其覆盖作用，同时永久保留原始变更与沉淀审计。

未来若部署数量和独立发版成本显著增长，可以引入外部 Catalog Distribution Service 分发签名、版本化快照，但它不接管产品归并、历史迁移、审计或本地 Override 语义。Heartbeat 必须缓存最后成功快照，不能把运行可用性依赖于分发服务实时在线。

Catalog 不携带规范图标；每个 Owner/App 继续保留首次上传的有效平台图标，归并时保留目标 App 图标。Provisional App 正常参与 Report、Timeline 与详情查询，避免未知产品的数据消失；Deployment Administrator 在这些视图中额外看到待归类标记和设置页入口，非管理员不显示内部分类状态。原始 AppIdentity 只在管理员管理页与诊断表面展示。

App Catalog 只拥有产品身份与规范名称，不拥有 Report、Timeline 或排名中的隐藏/统计策略。操作系统组件是否属于展示噪声由独立策略决定，不能通过向 Catalog 增加行为字段解决。

每次 Catalog 协调与管理员 Override 变更都写追加式审计记录，包含目录版本、内容哈希、变更摘要、操作者和时间；记录永久保留，设置页展示最近历史。若数据库已应用的 Catalog 版本高于当前二进制携带的版本，backend 进入回滚兼容模式：允许启动、保留数据库中的较新映射、跳过目录降级并明确告警，绝不按旧目录反向拆分数据。

## Consequences

- ✅ 已收录产品在 Windows 与 macOS 首次出现时即可落到同一 App。
- ✅ Catalog 升级会自动修复严格命中身份的历史数据，同时保留本地覆盖。
- ✅ 稳定 App Key 与只增不删的映射策略避免知识引用随目录版本漂移。
- ✅ 多 backend 实例不会并发重复协调，同一 Catalog 内容具有可验证的应用状态。
- ✅ 服务端版本回滚不会撤销已经生效的较新产品映射。
- ✅ 目录与管理员变更具备长期可追溯的审计链。
- ✅ WebUI 产出的候选必须经过代码审查，运行中的部署不能绕过发布流程替换内置目录。
- ✅ 管理能力在设置页可发现，但部署管理员授权的 root of trust 不下放给产品 UI。
- ✅ 未分类产品的数据仍完整可见，同时分类状态不会泄漏给非管理员。
- ⚠️ App Catalog 必须具备版本演进、冲突检测和可重复协调语义，不能退化成散落在采集端的硬编码别名。
- ⚠️ Catalog 或协调逻辑错误会阻止 backend 启动，因此必须有集成测试覆盖升级与回滚路径。
- ⚠️ Catalog 版本和哈希成为部署兼容性约束，发布流程必须同步验证二者。
- ⚠️ 回滚兼容模式不会应用旧二进制携带的 Catalog 变更，运维必须关注告警。
- ⚠️ 未来外部分发 Catalog 时需要签名、缓存与离线回退，不能直接把通用配置中心当作领域真相源。
- ⚠️ 未知或私有应用仍会作为 provisional App 出现，等待部署管理员分类。
- ⚠️ 产品渠道默认分开会产生更多 App；希望合并的部署需要显式 Override。
- ⚠️ 产品图标仍取决于各 Owner 首次上传的平台资产，跨 Owner 或跨部署不保证完全一致。

## References

- [ADR-034](./034-app-as-cross-platform-product.md) — App 与 AppIdentity 双层模型
- [`server/CONTEXT.md`](../../server/CONTEXT.md) — App Catalog 与 Deployment Administrator 术语
- [`AppMergeService.cs`](../../server/Heartbeat.Server/Services/AppMergeService.cs) — 现有事务化产品归并能力
