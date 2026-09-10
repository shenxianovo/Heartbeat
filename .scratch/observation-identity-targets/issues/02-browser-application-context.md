# 02 — Browser 窗口观测与应用上下文归属

**What to build:** Browser 保留按窗口独立观察页面活动的行为，事实长期归属设备上的 App 应用上下文。关闭、重开窗口或重启浏览器后，历史仍可按设备及 App 查询。

**Blocked by:** [01 — System 观测身份与设备归属完整链路](01-system-observer-target.md).

Status: done

**Parent:** [观测身份与事实归属改造](../PRD.md)

## 实施约束

窗口是临时 FOI，不登记为长期对象。App 是跨设备产品；应用上下文由同一 Owner 下的设备与 App 共同辨认，跨启动保持。不同 Profile 共用此上下文，但独立扩展安装仍是独立 Observer。

复用任务 01 的 Observer/Target 契约。扩展安装持久身份负责辨认 Observer；设备身份和已有平台应用标识用于解析应用上下文，不能要求离线采集前在线创建记录。保留窗口模型与最小 Segment SDK 的职责，窗口号只帮助并行活动与必要的事实细节表达。

现有 Owner、Stream、FactId 身份与 Revision 不变。同一 Target 不合并不同窗口或 Observer 的事实。App 产品纠错/合并需要维护上下文引用，但不能合并事实。

## Acceptance criteria

- [x] 两个窗口同时活动时分别产生事实，窗口细节保留，二者 Target 为正确的设备与 App 上下文。
- [x] 浏览器及窗口重开不改变应用上下文；关闭窗口不会使已有事实丢失或查询失效。
- [x] 独立扩展安装的 Observer 保持独立且跨重启稳定；同设备同 App 的 Profile 差异不会拆分应用上下文。
- [x] 发布、缓存恢复、上传确认、Analytics 摄入与查询完整传递 Observer/Target，旧待发快照重放保持原事实身份。
- [x] 按设备查询包含其应用上下文事实，按 App 查询返回正确产品；App 合并/纠错后无悬空引用或事实丢失。
- [x] 有设备与 App 依据的历史 Browser 数据完成映射；缺少 App 依据的历史保留已知设备归属，不用名称猜产品。
- [x] 页面选中仍不代表 OS 前台或注意力；Browser 数据不会被新归属路径计入 System Report 时长。
- [x] 自动验证及本地开发 Browser 链路验收有可复现证据，明确真正检查过的窗口/重启场景。
- [x] 记录本批已迁移消费者与仍需保留的旧输入转换，完成适用构建、review 与提交。

## Validation

复用窗口模型、fold、Segment SDK 和协议集成测试验证并行窗口与恢复；从 Runtime 发布到 Analytics HTTP 与查询验证归属，不能仅断言扩展内存状态。以旧持久快照验证缓存升级与重放，以已有 App 维护入口验证合并/纠错后的查询。

沿用本地开发套件执行实际 Browser smoke；自动结果与实际浏览器结果分别记录。本批完成后 Browser 新写入走新契约，历史兼容的最终退出由任务 05 验证。

## Comments

2026-09-10：依赖公共链路；与 VRChat 改造没有互相阻塞关系。

2026-09-10（实施收尾）：02 实现、自动验证、真实本地 Browser 链路和两轴 review 已完成，
随本次实现提交标记 done；01 的真实 System/Windows 门禁仍是 ready-for-human。
父 PRD 继续由 03–05 承接开发与切换，不标 done。未改变已确认设计，未引入 DataSource。

## 实施与验证（2026-09-10）

修改存储前已向用户说明具体表/唯一键、离线引用、历史映射与 App 维护方案。
局部实现、当前消费者、旧协议/缓存/历史边界与退出条件详见
[Browser 实施记录](../../../docs/architecture/browser-observation-targets.md)。

### 自动验证

- TDD 首先确认 Browser HTTP 用例因不接受应用上下文 Target 失败、App 合并因引用约束失败、
  缓存恢复丢失元数据及 Dashboard 因 Target 不同丢失相关 Browser 观察，再分别实现至通过。
- `FactHttpTests.BrowserWindows_KeepIndependentFactsInOneApplicationContext_AndReplayOldSnapshots`：
  双窗口独立事实、当前与旧表示重放、设备/App 查询及无 Subject 的新入口。
- `FactHttpTests.BrowserRuntime_V4CacheUpgradeAndNativePublish_ReachHttpQueriesWithStableInstallationAttribution`：
  实际 Browser package 的 ExternalHost→Runtime，旧 v4 缓存升级、重启、同修订发布、HTTP 摄入、
  查询和 custody ACK，验证同 FactId、完整 Observer/Target 与新活动字段。
- `BrowserApplicationContextTests`：同设备/App 并发安装共用上下文但 Observer/事实独立；
  Owner 隔离、数据库唯一性与直接 SQL 跨 Owner/删除引用拒绝；产品合并、部分平台身份纠错后
  Target 与设备/App 查询正确，旧/新快照收敛且事实身份/修订不变，Browser 不进入 System Report。
- `BrowserApplicationContextMigrationTests`：Segment/Event 两家族真实 PostgreSQL 从 01 基线升级，
  保留表 OID、行 Id、FactId、Revision、时间和原 Payload；仅凭已有 AppIdentityId 也能映射；
  无 App 依据保留 device、未知 Observer 保留 null；重复升级与旧/新重放不丢失或复制事实。
- Browser 协议/fold/delivery/cache/host 集成：持久安装 UUID、跨启动恢复、首次绑定先落盘、
  完整快照 ACK 比较及旧 identityKey 读取；前端覆盖设备/App 产品关联和独立 Target 泳道。
- 最终 `dotnet test Heartbeat.slnx --no-restore`：13 个项目，**1,284 项全部通过**。
  全量首轮发现原非法 payload fixture 使用现已合法的 activityKey，改用真正未知字段后重新
  完整运行通过；没有放宽投影拒绝规则。
- `dotnet build Heartbeat.slnx --no-restore`：**0 warning / 0 error**；
  `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 通过。
- Browser `npm test`：**111 项通过**；`npm run build` 的 TypeScript/Vite 通过。
  前端 `npm --prefix frontend run verify`：**289 项通过**，类型检查与构建通过。
  `node scripts/collector-contracts.mjs check` 通过。OpenAPI 客户端从实际当前 Analytics 重新生成。

### 真实 Browser 验收

使用 [可复现脚本](../../../scripts/smoke-browser-observation-targets.mjs) 与本地开发套件准备的
独立 Profile，运行真正 Google Chrome headless/MV3 扩展、原生 macOS Desktop、独立 Analytics
进程和临时 PostgreSQL 18；没有用 TestHost 代替此次真实链路。两个实际窗口生成独立 FactId，
共享一个应用上下文；按设备/App 联合查询命中；关闭对应窗口后其历史仍可查。

随后停止 Desktop，在真实扩展离线队列中取得新事实快照，再重启 Chrome 和 Desktop；
同 FactId、ObserverId、TargetId 成功到达 Analytics，扩展安装 UUID 不变。
最终脚本退出码 0，完成时间为 **2026-09-10 13:39:50 UTC**，
具体结果见 [脱敏报告](../browser-smoke-report.json)，环境前提和复现命令见实施记录。

范围说明：真实重启验证针对离线旧快照；重启后新活动及独立安装共用 Target 由自动测试覆盖，
没有声称真实多 Profile 或 Windows/Edge 验收。页面选中不证明 OS 前台或注意力；本次真实 smoke
关闭 System 采集，不关闭 01 的真实窗口/输入与 Windows 门禁。生产副本迁移、旧版本退出和
最终清理仍由 05 承接；02 的唯一性、引用完整性、设备/App 查询与旧快照正确性已在本批解决。

### Review 与 tracker closeout

Standards：0 项规范违反；1 项非阻塞 P3（多处 EF Target 维度投影重复）。基于实测 EF 翻译约束
保留显式查询表达式，相关 Report 回归通过。Spec：0 项确认缺陷、无范围扩张；补审脚本及
最后的测试样本修正也通过。完整说明与 friction closeout 见实施记录。

同步父 PRD、Collection/Analytics/Frontend context 和兼容债务清单；01 的旧阶段说明追加当前
Browser 实施指针。用户原有未提交的模型文档及后续票据草稿保留，不混入本任务提交。
