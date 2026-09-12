# Track 获取与首个协议

状态：已实现并验证

## 目标

在已注册 Collector 下获取或创建稳定 Track，并定义 `desktop.application.foreground` v1，作为桌面采集完整链路的第一个协议。

## 契约

- `POST /api/v1/collectors/{collectorId}/tracks`，请求只含 `type` 与 `version`。成功始终返回 `200` 及 Track 的 `id`、`collectorId`、`type`、`version`、`timeMode`、`endMode`、`createdAt`。
- Owner 从已验证令牌取得；Collector 必须已存在并属于该 Owner。不存在和不属于该 Owner 都返回 `404 collector_not_found`，不隐式创建父级。
- `(collector_id, type, version)` 是唯一地址。重复及并发获取复用 ID 和创建时间，不修改既有 Track。
- 时间行为来自代码中的协议定义，请求不能指定时间模式、Track ID、创建时间或其他字段。协议按精确名称及版本查找，名称去除首尾空白后查找，不进行大小写转换或版本回退。
- 参数无效返回 `400 invalid_request`；未支持的协议或版本返回 `400 unsupported_protocol`；未认证返回 `401`。
- 已有 Track 的时间模式与已登记协议不一致时返回 `409 track_protocol_conflict`，保留现有数据以便诊断。
- 协议登记先使用静态代码定义，无数据库表、插件发现机制或设备管理模型。唯一初始协议为桌面前台应用 v1，采用 Explicit Range，允许持续状态续期，并提供载荷校验器。
- Infrastructure 先执行带 Owner 限制的 `INSERT ... ON CONFLICT DO NOTHING`，再通过独立语句读取归属该 Owner 的 Track。默认 PostgreSQL Read Committed 下，后一次读取可以看到冲突时等待提交的并发插入结果。
- Track 获取是 Collector 接入准备，不在每条 Record 上传时重复调用。不改动当前持续状态写入 SQL。

## 验证

- 协议：时间行为、未知类型和版本、有效原生标识、缺失与越界字段、错误 JSON 类型。
- HTTP 与真实 PostgreSQL：注册 Collector 后获取 Track、重复与并发幂等、跨 Collector 复用协议、Owner 隔离、请求字段约束、未认证与已有 Track 协议冲突。
- 不修改 EF 模型或 migration，不操作业务数据库。

## 后续

批量 Record 上传接口调用协议校验器及续期能力定义，再接入当前持续状态存储。之后实现重放查询和首个 macOS 桌面 Collector。

## Comments

- 2026-09-12：首个 HTTP 测试先确认返回 404（路由尚未实现），随后接入端点和持久化。
- 2026-09-12：新增 14 项协议测试和 14 项 HTTP/PostgreSQL 集成测试。复用原 Collector HTTP 测试的宿主与认证配置，现有 Collector 用例保持通过。首次回归的集成测试因 Testcontainers 清理容器初始化超时未能执行；重跑后 `dotnet test Heartbeat.slnx --no-restore` 全部通过：Domain 40/40、Integration 55/55，无 warning。未修改 EF 模型和 migration，未迁移业务数据库。
