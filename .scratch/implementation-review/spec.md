# 业务用途审查的待讨论项

Status: needs-info

已完成的清理、登录流程和验证结论见 [业务覆盖](../../docs/validation/business-coverage.md)、[ADR-0019](../../docs/adr/ADR-0019-single-owner-hub-management.md) 和 [Hub 验收记录](../../docs/validation/hub-management.md)。保留 next_record 的决定见 ADR-0001；HTTP 外部 Collector 接入和 Timeline 名称均已确认保留。

独立 macOS 命令行采集不作为产品入口，用户要求评估收掉重复启动路径；具体宿主迁移尚未确认，未实施。旧 candidates.md 是 2026-09-17 的线索，不作为当前缺陷结论。

## B04 的具体收简边界（待确认实施）

1. 产品使用入口收敛到原生 Desktop 与服务器 Hub，不再要求桌面用户配置第二套命令行 Collector 身份、Hub 地址和令牌。
2. 仍需保留三种独立验证目的：真实 macOS 观察器到 HTTP Hub 接管/交付；有人操作的真实采集观察；窗口标题读数诊断。后台 runtime-replay 的 OS 输入受控，不能替代这些证据。
3. 建议让独立原生进程成为 DevCLI 内部验证宿主，复用现有观察器与 DesktopCollectorSession；生产 Collector 项目只提供采集能力。DevCLI 负责临时身份、端点和进程生命周期，不新建采集实现或测试网络接口。宿主必须保留 MacRunLoop 的原生主线程事件处理，不能简单挪进 DevCLI 当前的 async Main。
4. HTTP Hub、HubSubmissionClient、接管/回执/故障行为测试继续保留。移除产品 CLI 不意味着移除可组合的跨进程接入边界。
5. 这涉及验证宿主责任迁移，需确认实施范围并更新 ADR-0013/0025、场景文档与 Feature Map。当前没有迁移 Program，也没有删除这些场景。

## 其他待评估项

- Collector.Create 的测试构造重载：不是废弃实体，若整理应避免复制不变量校验。
- 永久失败 Record 保留和查询有价值，人工重试、纠错、删除的业务闭环尚未确定。
- VRChat 目前使用通用 JSON 回放，是否补专用展示待决定。
- 主题手动切换后缺少回到系统模式的入口，保留现有 system 能力。
- 真实 VRChat 账号验收见 [issue 03](issues/03-vrchat-authentication-flow.md)。
