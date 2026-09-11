# 07 — 对象关系与产品维护贯通

**What to build:** 无旧 Stream 的新事实能够按设备、App、账号和本人准确查询；人工关联和产品合并/重绑维护后，事实仍可幂等重放并保留原始证据。

**Blocked by:** [01 — 独立观测保存与读取](01-independent-observation-custody.md).

Status: done

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 通过 01 的真实原生 HTTP 入口建立无旧 Stream 的事实，贯通对象筛选、维护入口与后续读取；本项可独立于第一方生产者切换验证。
- [x] 设备、App、账号、本人及 FOI 查询覆盖新旧事实；不存在可选展示资料时仍能读取合法对象事实，不因缺少旧 Stream/Subject 或数字资料引用而漏行。
- [x] 同时发生的同 App 多设备 Facts 仅沿绑定准确 Fact 的关系归属；同 FOI 或时间重叠不造成串联，不把账号所属服务推断为设备运行关系。
- [x] 高 Revision 更新关系和缩短区间后，查询立即反映该快照的成员与时间；重试不重复生成关系，错误角色/种类/基数及跨 Owner 引用拒绝并保持原子性。
- [x] 人工 used-by 关系经实际创建、修改、移除入口维护，按确认适用区间影响本人视图；原 Facts 的身份、Revision、Result 和时间不被人工关联改写。
- [x] App 产品合并、部分平台身份重绑及删除覆盖后的回退沿真实 AppIdentity 依据作用于对应事实/关系，保留 Fact Id、Revision、Result、时间及准确产品引用。
- [x] 产品维护后旧产品 Key 或原平台身份的相同快照重放通过规范身份解析保持幂等，不复活已合并产品；不将目录维护误判为 Collector 更换实际 FOI。
- [x] 对象维护/查询的 API、展示调用方及相关 DTO/生成客户端均支持新契约；对缺资料、空关系、账号无设备及 Owner 隔离的行为可验证。
- [x] 对应关系、本人关联、App 目录、查询及 UI/客户端回归通过并记录证据；全量报表、回放及知识分析由 08 独立承接。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。


### 2026-09-11 实施与验收

基线 `6976e10093c3d91623e48df617df5016fb4bcb70`，分支 `codex/observation-ticket-07`。

- 新增 11 项独立 HTTP + 隔离 PostgreSQL 回归：本人 5 项、产品维护 6 项；复用并复验 01 的 8 项 IndependentRelations。新事实均经 `/api/v1/observations` 保存，不生成 Stream/Subject。
- 本人查询先稳定复现 Segment/Event 的 Kind 为空（2 项失败），补齐家族投影并将 PersonSourceCount.Source 声明为 nullable。无展示资料的账号按 FOI UUID 读取及人工关联创建/修改/移除通过；原 Id、Revision、Result、完整时间不变。直接 Person、同 App 多设备、关系换设备/移除与缩短区间及时影响本人视图。
- 产品维护先稳定复现 2 项失败：移动最后一个平台身份会删除仍由直接产品引用事实使用的 App，后续读取 AppId 变 null。重绑后仍被 Facts/RelationMembers 引用的产品现在保留，避免无平台证据事实丢失资料及重放身份漂移。
- App/机器 FOI × 内置目录/未知平台身份覆盖 merge、部分 rebind、删除 Override 后 catalog/provisional 回退；相同原平台身份与先前接受的旧产品引用重放通过，不复活旧产品。维护未修改 AppReferenceEvidence，仍适用的精准位置/raw reference/平台依据保留；既有替换或移除关系的三项回归确认旧身份不粘滞。
- 本人页面明确显示“未提供来源”。先复现 input Event 的 result alias 泄漏与未知 Event 标题被解释（2 项 UI 失败），再按 input Aspect 同时隐藏 payload/result；其他未知 Aspect 的原始结果完整呈现，标题不猜测。账号缺名字回落稳定 key。

验证（只使用隔离测试 PostgreSQL；未操作业务库）：

- `dotnet tool restore`、`dotnet restore Heartbeat.slnx`、`dotnet build Heartbeat.slnx --no-restore`：成功，0 errors。首次 no-restore 构建因新工作区缺 assets 失败，完成 restore 后通过。
- `dotnet test server/Heartbeat.Server.Tests --no-restore --filter 'FullyQualifiedName~IndependentProducts|FullyQualifiedName~IndependentRelations|FullyQualifiedName~AppCatalog|FullyQualifiedName~AppMerge' --nologo`：61/61 通过。
- `dotnet test Heartbeat.slnx --no-build --nologo`：13 个测试项目共 1,381 项通过，0 失败、0 跳过；涵盖旧事实、角色/种类/基数拒绝、事务回滚、Owner 隔离、本人关联、目录/产品及对象查询回归。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `npm run verify --prefix frontend`：类型检查、44 文件/293 测试、生产构建全部通过。本人页面 6 项通过；相关 App Catalog UI 回归包含于全量前端测试。
- 使用隔离本地 Development 后端 OpenAPI、NSwag 14.7.1 临时重新生成 client 后，前端 typecheck 也通过；按协调者明确分工恢复生成文件，不在本提交写入全量 client，08 在整合时统一生成。契约交接：FactResponse 的 Kind/Result 与 nullable 旧键继承 01；本项新增 PersonSourceCount.Source nullable，PersonFactQuery 返回正确 Kind，无新端点。
- `git diff --check`：通过。

审查与 friction closeout：

- `/code-review` 固定点 `6976e10`，规格本 Ticket07；为最终仅一个实现提交，双轴审查使用包含新测试的工作树 diff。Standards 0 项发现，Spec 0 项发现；两轴无未关闭项。
- 测试此前只检查旧 Event payload，未覆盖新 result alias；本项补入双字段隐私断言和未知 Event 无损断言。产品旧回归只看有平台身份的 Facts，本项补入同时存在直接产品引用的事实，验证维护不能删除仍使用的产品资料。
- 本项无新兼容分支或人工门禁；既有 payload alias 服务当前读取调用方，退出仍由 01/08 与父 PRD 的兼容台账承接。最终生成 client 与全量消费由 08 负责，已向协调任务交接；父 PRD、ORCHESTRATION 及其他 issue 按授权不在本分支修改。
- 未推送、部署、操作业务库或恢复暂停的生产演练；外部上线门禁仍归 observation-storage PRD。
