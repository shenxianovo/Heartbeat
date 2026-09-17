# Heartbeat

Heartbeat 把一个人在数字世界中的异构活动痕迹记录为时间有序的观测轨道。

**Collector 采集，Hub 交付，后端存储，前端展示。** Collector 只向 Hub 提交逻辑声明与 Record；Hub 持久接管后完成后端注册、Track 映射和上传。

当前重写完成前不部署，不保留旧接口、旧数据格式或旧客户端的兼容实现。

## 文档

- [领域语言](CONTEXT.md)：Heartbeat 记录领域的核心术语。
- [架构决策](docs/adr)：已经接受的关键设计决策，新增 ADR 使用 [仓库模板](docs/adr/ADR-TEMPLATE.md)。
- [记录存储模型](docs/recording-storage-model.md)：`Timeline -> Collector -> Track -> Record` 四层模型、字段和约束。
- [记录 HTTP 接口](docs/recording-api.md)：Collector 注册、Track 获取、Record 上传和 Track 级重放查询。
- [桌面前台应用协议 v1](docs/protocols/desktop-application-foreground-v1.md)：前台应用读数的 value 结构与区间断开规则。
- [桌面前台窗口协议 v1](docs/protocols/desktop-window-foreground-v1.md)：与前台应用分开的窗口标题观测。
- [桌面离开信号协议 v1](docs/protocols/desktop-system-away-v1.md)：锁屏、会话失活与休眠的独立原因区间。
- [桌面输入事件协议 v1](docs/protocols/desktop-input-event-v1.md)：非文本物理键鼠事件。
- [桌面观察状态协议 v1](docs/protocols/desktop-observation-status-v1.md)：各项采集能力的历史可用状态。
- [持久化实现](src/Backend/Heartbeat.Infrastructure/Persistence/README.md)：EF Core / PostgreSQL 映射约定。
- [macOS Collector](src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md)：最小桌面 Collector 的运行方式。
- [Hub 记录交付](docs/hub-record-delivery.md)：SQLite 持久接管、后台上传、恢复及桌面接入。
- [Web 前端](src/Frontend/Heartbeat.Web/README.md)：本地运行、登录和 Record renderer 扩展。
- [本地开发](docs/development.md)：统一 Docker 启动、热更新、生产镜像验收与首次配置。
- [工程验证](docs/verification.md)：Git 变更选择、结构质量闸门、可复现场景与证据目录。
- [未决设计](docs/recording-open-questions.md)：未交接数据、断采规则、设备关联等尚未确认的问题。
- [验收记录](docs/validation)：带日期的系统、平台能力和回放体验验收事实。
- [Agent 规则](AGENTS.md)：协作约束和本仓库的工程规则。
- [Agent 协作细则](docs/agents)：[issue 追踪](docs/agents/issue-tracker.md)、[triage 标签](docs/agents/triage-labels.md)、[领域文档布局](docs/agents/domain.md)、[收口检查](docs/agents/closeout.md)。

## 本地运行

```bash
./scripts/heartbeat-dev env up
```

默认启动 Web、API 和 PostgreSQL，访问 <http://localhost:3000>。也可以显式选择服务：

```bash
./scripts/heartbeat-dev env up api
./scripts/heartbeat-dev env up hub
./scripts/heartbeat-dev env up desktop
```

首次启动 Hub 或 Desktop Collector 前运行：

```bash
./scripts/setup.sh
```

服务组合、release 模式、日志、重置和配置见[本地开发](docs/development.md)。
