# Heartbeat Feature Map

本页只用于选择验证入口，行为语义以链接的长期文档为准。

| 能力 | 用户入口 | 验证入口 | 权威文档 | 证据限制 |
| --- | --- | --- | --- | --- |
| 桌面客户端 | Mac `Heartbeat Dev.app`，连接设置、采集、菜单栏 | `scenario desktop-replay` | [客户端 README](../../../../../src/Desktop/README.md)、[宿主责任](../../../../../docs/adr/ADR-0016-desktop-host-and-credentials.md) | 真实应用包与服务；需界面操作及 OIDC 登录，不覆盖 Windows、发行签名或自动更新 |
| 本地环境 | `env up [web\|api\|db\|hub\|desktop]` | `env status --json`、定向 `env logs`、`env down` | [本地开发](../../../../../docs/development.md) | `desktop` 打包并打开 macOS UI，Hub 同进程运行；`env reset` 默认只预览 |
| 回放体验 | Web `/`，OIDC `/auth/callback` | `scenario replay-fixture`；活动泳道性能用前端 `npm run test:perf` 的生产模式 | [前端 README](../../../../../src/Frontend/Heartbeat.Web/README.md)、[展示归属](../../../../../docs/adr/ADR-0011-web-timeline-presentation-ownership.md)、[记录接口](../../../../../docs/recording-api.md)、[体验验收](../../../../../docs/validation/experience-visualization.md) | 认证和 API 均为 mock；基准记录测量值，不设性能闸门 |
| Record 交付与查询 | [HTTP 端点](../../../../../docs/recording-api.md) | `scenario delivery` | [记录接口](../../../../../docs/recording-api.md)、[存储模型](../../../../../docs/recording-storage-model.md) | 覆盖 API 与数据库，不覆盖 Hub 或浏览器 |
| Hub 接管与原生采集 | `env up hub`、独立 Collector CLI | `scenario native-desktop` | [Hub 交付](../../../../../docs/hub-record-delivery.md)、[macOS Collector](../../../../../src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md)、[系统验收](../../../../../docs/validation/system-acceptance.md) | 默认不保存原始 Collector 日志；权限、物理输入、锁屏和休眠需要人工 |
| Collector 启动到落库 | macOS Collector 启动、Hub 接管、注册与上传 | `scenario collector-delivery` | [工程验证](../../../../../docs/verification.md#collector-到落库)、[Hub 交付](../../../../../docs/hub-record-delivery.md) | 真实原生快照与存储链路，需要有效 Auth；不覆盖持续采样、平台交互或 Web |

所有命令前加 `./scripts/heartbeat-dev`。场景证据位置和敏感数据规则见[工程验证](../../../../../docs/verification.md)。
