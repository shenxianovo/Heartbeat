# ADR-058: 数据库迁移作为部署 CI 的独立阶段

Status: Accepted — 2026-09-10，取代 [ADR-013](013-reenable-auto-migration.md)。

Analytics 部署仍由 Owner 手动触发一个 workflow，但由 CI 按构建/测试、停写、备份、迁移、
启动和健康检查依次完成。Migration 从生产 HTTP 启动移出，使用本次构建的同一个不可变镜像。
独立手动迁移容易遗漏，仓库曾因此出现缺表；把迁移绑回服务每次重启又无法独立判断迁移结果。

`--migrate` 包含 EF 追加迁移及 KnowledgeIdentityBackfill、AppKnowledgeBackfill 两段 C# 回填；
完成后退出，不启动 HTTP、鉴权或 App Catalog。生产启动和 `--check-database` 只检查 migration
历史是否与镜像一致；未知的新版本、待执行迁移或未生成 migration 的模型都拒绝启动。
System 声明播种和 App Catalog 协调仍属于服务启动，不是一次性 schema 迁移。
Development 保留自动迁移，以兼容当前本地开发和隔离测试入口。

现有 Observer/Target 迁移会回填存量事实，不能在旧 Analytics 继续写入时执行。
CI 先停止唯一 Analytics 写入口，Collector 保管未确认快照；迁移失败不自动启动旧镜像或执行
Down，恢复依赖升级前备份。整个 workflow 串行且不取消正在进行的发布，服务器另有进程锁、
迁移容器占用检查和镜像成功凭据，防止断连后的重试重叠或跳过失败回填。

Owner 明确要求适应 1C1G 服务器：迁移专用 SQL 超时默认 0（无限等待），结束后恢复原命令
超时，不扩大普通请求预算。迁移 SSH 等待 350 分钟、job 360 分钟；服务启动健康等待 30 分钟。
这取代后续发布沿用的十分钟停写上限；真实 SQL/进程错误仍立即失败，CI 平台时间上限仍存在。
现有历史演练的 600 秒标准保留为当时证据，不能冒充当前部署预算或最新 Target 迁移的验收。

操作、失败恢复及对观测改造的承接见 [迁移 Runbook](../runbooks/analytics-database-migration.md)。
