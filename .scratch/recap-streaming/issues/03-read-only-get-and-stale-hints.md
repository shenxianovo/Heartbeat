# 03: GET 退成纯读 + 三态与判脏提示位

Status: done

## Parent

[PRD](../PRD.md) · [ADR-042](../../../docs/adr/042-recap-streaming-generation.md) §2/§3

## What to build

让 `GET /api/v1/recaps/daily` 变成可断言"不烧 token、不写库"的纯读端点，并把原先藏在它里面的
自动重生成改成提示位。

### 服务端

- `RecapService`：把 `GetDailyRecapAsync` 拆成
  - **读路径**（认证 owner 用）：读缓存 + 判空 + 判脏，全部确定性、零 LLM、零写库；
  - **生成路径**（issue 04 用）：装配投影 → 流式生成 → upsert。
- `force` 参数从 GET 上取消；`IsFreshAsync` 里的水位比较不再触发生成，而是产出
  `segmentStale`。**1 小时阈值仍留在服务端**（防轮询烧 token 的护栏不能交给前端）。
- 判空要便宜：用段存在性查询，**不做完整投影装配**（完整装配只属于生成路径）。
- `DailyRecapResponse` 三态（不新增 `notGenerated` 布尔）：
  - `isEmpty=true` → 空日；
  - `isEmpty=false && narrative == null` → 有数据但从未生成；
  - 否则 → 有叙事，附 `generatedAt` / `model` / `segmentStale` / `knowledgeStale`。
- 新增 `SegmentStale`，与既有 `KnowledgeStale` 平铺同构；**不做 `staleReasons` 数组**。
- 公开路径 `GetCachedDailyRecapAsync` 语义不变（仍只读缓存、两个判脏位恒 false、未生成 → 404）。

### 契约文档

- `docs/api.md`：补三态表 + 一句"生成只由 POST 触发，GET 永不调用 LLM、永不写库"。
- 重新生成 NSwag 客户端（GET 的 DTO 变了）；`frontend/src/api/index.ts` 的 `fetchDailyRecap`
  去掉 `force` 参数。

## Tests

- 从未生成的非空日：GET 返回 `isEmpty=false, narrative=null`，且**生成器调用次数为 0**。
- 空日：`isEmpty=true`，生成器零调用，且不写缓存行。
- 今日缓存水位落后 >1h：GET 返回 `segmentStale=true` 且不重生成（旧行为的反向断言，防回归）。
- 历史日缓存命中：`segmentStale=false`，`knowledgeStale` 仍按 hash 计算。
- 访客路径行为不变。
