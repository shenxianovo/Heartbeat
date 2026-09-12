# Collector 注册接口

状态：已实现并验证

## 目标

提供一个完整纵向切片，使 Collector 能在已认证 Owner 的既有 Timeline 中解析或创建稳定绑定，并获得服务端生成的 Collector ID，供后续 Track 获取使用。

## 已确认的契约

- 纵向切片包括 Application 用例、HTTP 端点和 Infrastructure 持久化；HTTP 层只负责传输协议映射。
- Timeline 必须预先存在；注册 Collector 不隐式创建 Timeline。
- HTTP 调用者认证为 Owner，Application 用例根据 `owner_id` 解析其唯一 Timeline；客户端不能指定 `timeline_id`。
- 认证复用 `main` 分支的双 Bearer 方案：`TokenSelector` 根据 JWT header 的 `typ` 把 OIDC access token（`at+jwt`）交给 `OidcBearer`，把 agent session token（`JWT`）交给 `SessionBearer`；选中的 JwtBearer scheme 必须完成正常验签。
- 两种令牌共用 `https://auth.shenxianovo.com` 的 Authority/JWKS。Owner 身份只从验签后 principal 的 `sub` claim 取得，不接受客户端提交 Owner 身份。
- Auth `sub` 必须能解析为 UUID，并作为领域 `OwnerId`。Heartbeat 不兼容 `main` 中任意字符串形式的 Owner ID。
- 缺失 `sub` 或 `sub` 不是 UUID 时，两个 JwtBearer scheme 都在 `OnTokenValidated` 阶段使认证失败，端点返回 `401`。Application 不接收未验证的字符串 Owner ID。
- 认证配置沿用 `main` 的环境变量映射：
  - `AUTH_AUTHORITY` → `AuthService:Authority`
  - `AUTH_ISSUER` → `AuthService:Issuer`
  - `AUTH_AUDIENCE` → `AuthService:Audience`
  - `AUTH_OIDC_ISSUER` → `AuthService:OidcIssuer`
  - `AUTH_OIDC_CLIENT_ID` → `AuthService:OidcClientId`
  - `AUTH_OIDC_AUDIENCE` → 可空的 `AuthService:OidcAudience`
- 注册只处理 Collector，不同时声明或创建 Track。
- `(timeline_id, key, target)` 是稳定地址。地址不存在时创建 Collector，已存在时返回原 Collector。
- 重复注册会更新 `display_name`；并发更新时，以最后成功提交的值为准。
- Collector 负责提供规范化的 `target`；Heartbeat 只去除首尾空格并按完整字符串精确比较。
- Heartbeat 只验证 `key` 的命名格式，不要求它预先出现在 manifest allowlist 中。
- `key`、`target` 和 `display_name` 均不得超过 255 个字符。
- 响应不暴露本次是创建还是复用，始终返回规范化后的 Collector，包括 `id`、`key`、`target`、`displayName` 和 `createdAt`。
- HTTP 入口是 `POST /api/v1/collectors`。请求只包含 `key`、`target` 和 `displayName`，不接受客户端提供的 `id`、`timelineId`、`createdAt` 或 Track 声明。
- 成功始终返回 `200 OK` 和规范化后的 Collector 表示。
- 错误使用 RFC 9457 Problem Details：未认证返回 `401`；字段缺失、格式或长度无效返回 `400`；Owner 尚无 Timeline 返回 `409` 和稳定错误码 `timeline_not_provisioned`；未分类基础设施异常返回 `500`。
- Infrastructure 使用 PostgreSQL 原子 `INSERT … ON CONFLICT … DO UPDATE … RETURNING` 实现并发安全的注册。
- HTTP 端点在功能恢复阶段先使用 Minimal API；待整体 API 形态稳定后再统一评估迁移到 MVC Controller。
- Application 对外暴露 `IRegisterCollector`，以 `RegisterCollectorCommand` 接收注册字段，并用显式 `RegisterCollectorResult` 区分成功与 `TimelineNotProvisioned`。
- Application 通过用例语义明确的 `ICollectorRegistrationStore` 持久化；该端口封装 Timeline 定位、事务和 PostgreSQL upsert，不拆分为通用 repository。
- Application 返回不可变的 `RegisteredCollector` 投影，不向 API 暴露 EF 跟踪状态或领域实体。
- `IRegisterCollector` 注入 `TimeProvider`，每次调用只获取一次 UTC 时间并传给 Store。新建时用它生成 UUID v7 和 `created_at`；复用时保留原 `created_at`。
- Initial Recording migration 是尚未发布的 Clean Room 基线，直接修订它及 model snapshot，不新增 Alter migration。

## 验证

- PostgreSQL 集成测试覆盖首次注册、重复注册、显示名更新、Owner 隔离、Timeline 未 provision，以及两个并发请求最终只保留一行。
- HTTP 集成测试用测试认证 scheme 注入 `sub`，覆盖 `200`/`400`/`401`/`409` 和响应格式。
- 本地测试覆盖 `JwtTypeSniffer` 以及缺失或非 UUID `sub`；常规测试不请求真实 JWKS。
- 主 Agent 完成实现后，由 Luna Agent 运行测试并回报结果；主 Agent 只读取其测试结果，不代替它执行验证。
- 2026-09-12 由 Luna Agent 执行 `dotnet test Heartbeat.slnx --no-restore`：Domain 测试 26/26、Integration 测试 13/13，全部通过且无 warning。
- 2026-09-12 补充异常 JWT header、额外请求字段、400/401 Problem Details 和 Track 数据库时间约束的回归测试，先确认失败再修复。由 Luna Agent 执行全套测试：Domain 26/26、Integration 26/26，全部通过且无 warning；`dotnet ef migrations has-pending-model-changes` 确认模型与初始 migration 一致，未对现有数据库执行迁移。
