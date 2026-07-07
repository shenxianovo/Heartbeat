# ADR-020: system 采集器成为经由枢纽的一等 Source —— 退役 POST /usage

## Status: Proposed

## Date: 2026-07-07

(pending implementation)

## Context

### 服务端已经统一,客户端还剩一条平行管道

ADR-018 之后,服务端把 `/usage` 上传映射为 ActivitySegment(补 `source='system'`、代算 `SystemIdentity.Key`)再走统一摄入例程——system 路径在服务端早已是段管道,只是穿着 legacy 外衣。客户端却仍维持一条与插件段完全平行的管道:

| 关注点 | system 路径 | 插件路径 |
|---|---|---|
| 内存 buffer | `AppMonitorService._usages`(List,无上限,同 Id 快照可重复) | `SegmentIngestService`(Dictionary 按 Id 键控,20k 上限,到达即压缩) |
| compact 时机 | 出网前 fresh + cached 都压 | fresh 不压(依赖 buffer 已按 Id 收敛的跨文件不变量),cached 压 |
| 离线缓存 | `LocalCache`(cache.json) | `SegmentLocalCache`(segments-cache.json) |
| wire DTO | `AppUsageItem` → `POST /usage` | `ActivitySegmentItem` → `POST /segments` |
| 校验 | 仅服务端(`UsageValidationPolicy`,`EndTime > StartTime`) | 客户端 + 服务端(`SegmentValidationPolicy`,`>=` 允许点事件) |

`AppUsageItem` 是 `ActivitySegmentItem` 的真子集({Id, AppName, Title, Start, End} vs 多出 {Source, IdentityKey, Attributes});`SystemIdentity` 就在共享内核,客户端本来够得着。平行管道的具体成本:

- 同一个上传模板(fresh 上传→失败入缓存;cached 先重传→成功清空)写了三遍(usage / segments / input-events),compact 时机的差异一半是意外而非设计;
- hub 特性(Active 从流量推断、Deactivate 黑名单,`desktop/CONTEXT.md` 已定义待实现)天然不覆盖 system 源——内置采集器绕过了 ingest,而不是作为一等 Source 汇入同一漏斗;
- `SegmentIngestService.Accept` 拒收 `system` 的守卫,其存在本身就是两条管道分离的产物;
- drain-then-fail 丢数据模式(drain 清空 buffer 后 `cache.Add` 抛异常,已 drain 项蒸发)在三处重复且无测试。

### 信任澄清:退役 /usage 不产生安全回退

服务端 `SegmentController` 拒收 `source='system'` 只是皮带加背带——任何持 ApiKey 者本来就能经 `POST /usage` 写 system 轨。真正防"本机进程冒充 system 污染统计互斥轨"的是 **hub 侧**守卫(ADR-017 §1 信任模型)。删除服务端守卫、放行 Agent 自己上传 system 段,信任姿态不变。

## Decision

### 1. monitor 产段,AppUsageItem 退役

`AppMonitorService` 直接产出 `ActivitySegmentItem`:客户端算 `SystemIdentity.Key(AppName, Title)`;away 段同构(`source='system'`、`AppName='__away__'`、稳定 Id 快照生长,ADR-014/018 语义不变)。随之退役:`AppUsageItem`、`UsageUploadRequest`、`UsageValidationPolicy`(段校验由 `SegmentValidationPolicy` 独家负责,消灭双校验策略分叉)、客户端 `UsageUploadService`、`LocalCache`、`IUsageCache`。

### 2. 推模型,`ISegmentSink` seam

monitor 依赖一个单方法接口 `ISegmentSink`(喂入一批段快照),`SegmentIngestService` 是唯一生产 adapter。节律 = **闭合即推 + 进行中段 30s 定时快照**(常量,与 `TitleGateWindow` 同待遇,暂不进配置)——与插件采集器的"观测→折叠→推送"完全对称,system 成为真正内置的一等 Collector。`AppMonitorServiceTests` 的 17 个状态机测试换 fake sink,断言推出的段(测试意图不变,观察点迁移)。

### 3. 冒充守卫上移到 loopback 传输层

reject-system 从 `SegmentIngestService.Accept` 搬到 `SegmentIngestWorker`:它防的是"谁在调"(传输信任),不是数据合法性。`Accept` 变 source 无关,monitor 直接调用。**连带义务**:ingest worker 的请求处理(路由/JSON 解析/状态码映射/accepted 计数)提取为可脱离 `HttpListenerContext` 构造的可测单元——守卫不能搬进零覆盖区。插件作者的契约(404/400/500、错误体、`{"accepted":n}`)第一次获得测试。

### 4. 服务端同 PR 删 POST /usage

`UsageController` 上传入口、`SaveUsageAsync` 映射层、`UsageUploadRequest`、`UsageValidationPolicy` 及其测试删除;`SegmentController` 的 reject-system 删除(见信任澄清)。**GET /usage 查询投影保留**——它是 frontend Timeline 的 `usageData` 来源(`PublicUserController` 镜像同理),死的只是上传路径。单用户自更新舰队 lockstep 升级,ADR-018 已有先例。

### 5. 上传通道泛化("送达、缓存、或退回")

统一后剩两条流:segments 与 input-events(两个 adapter = 真 seam)。提炼泛型上传通道 module,契约:**喂入一批项,送达,或落离线缓存,否则原样退回**——退回项由调用方重注入源 buffer(hub 按 Id 收敛天然幂等;input buffer 重排队)。compact 作为按流策略(segments 出网前 `KeepLatest`;input-events 不压缩)。drain-then-fail 在通道内一次性修复并测试;两套 `HttpMessageHandler` 测试合一。`StatusUploadService` 不入通道(presence 是易逝信息,无缓存是设计而非缺失),顺势并入其 worker。

### 6. 细节默认值

- **图标上传挂点**:worker drain 后从段批次提取 distinct `AppName` 触发 `EnsureIconUploadedAsync`(行为与今天一致,数据源从 usage 批次换为段批次)。
- **旧 cache.json 孤儿化**(留日志):损失上界 ≈ 升级时刻恰好滞留在离线缓存的数据,单用户舰队接受;不为一次性事件养永久迁移代码。
- **托管服务注册顺序翻转**:worker 先注册、monitor 后注册,使 StopAsync(逆序)先停 monitor(终态快照推入 hub)再停 worker(最终 drain + 上传)。现状顺序在推模型下每次关机都丢尾巴——顺序依赖必须用注释钉住。

## Consequences

- ✅ Collection 只剩两条管道;新 collector 的边际成本 = 采集器本身(hub、通道、缓存、上传、幂等全复用)——ADR-017 的承诺补完。
- ✅ hub 特性(Active / Deactivate / 可观测性)自动覆盖 system 源,无需特例。
- ✅ compact 时机差异、双 wire 格式、双校验策略、服务端映射层全部溶解;净删代码(客户端 ~5 个类型 + 服务端 1 层)。
- ✅ drain-then-fail 从"三处未测的丢数据模式"收敛为通道契约的一条测试。
- ⚠️ 进行中段 EndTime 尾部新鲜度从 0(拉模型 drain 即快照)变为 ≤30s;闭合段仍零滞后,下个快照追平,统计无损。
- ⚠️ `AppMonitorServiceTests` 观察点从 `GetAndClearUsages()` 迁到 fake sink;断言形状变,行为覆盖不减。
- ⚠️ 升级窗口内旧 Agent 上传 404,数据滞留其离线缓存且旧格式孤儿化——损失上界一个离线窗口(单用户,接受)。
- ⚠️ 停机时序依赖托管服务注册顺序,脆弱点显式化(注释 + 测试钉住 monitor 停止时的终态推送)。

## References

<!-- Filled in as implementation lands -->

- Amends [ADR-017](./017-activity-segment-pluggable-collectors.md) §1/§2 —— 内置采集器从"绕过 hub"变为"经由 hub";数据模型与统计边界不变
- Amends [ADR-018](./018-stable-segment-identity-snapshot-upload.md) —— 双上传入口收敛为 `/segments` 单入口;快照契约(Id 即身份、单调生长)不变
- [ADR-008](./008-local-cache-offline-retry.md) —— usage 缓存(`LocalCache`)退役;离线重试语义由上传通道继承
- [ADR-006](./006-dedicated-report-endpoints.md) —— GET /usage 读端点不受影响
- [ADR-014](./014-away-detection-display-sleep.md) —— away 段语义不变,仅换产物形状
