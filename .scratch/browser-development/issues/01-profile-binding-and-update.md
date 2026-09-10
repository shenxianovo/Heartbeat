# 01 — Browser 开发连接绑定与更新

Status: ready-for-human

## 验收

- [x] 开发扩展缺少绑定时不发现生产 Desktop，端口缓存和端口复用不绕过 Profile 绑定。
- [x] 通用 Host 在处理任何开发协议请求前校验绑定；生产路径及具名 Collector 解耦保持。
- [x] 开发启动拒绝默认生产 Profile；绑定在目录锁下创建、跨重启保持。
- [x] 包更新仅允许 Runtime 首次 Activation 前执行；保留 Instance、配置及 Stream，使用不可变 Installation 和精确 hash。
- [x] 固定开发扩展目录保留绑定，构建失败不替换旧产物，目录切换中断不能绕过 Profile 固定关系。
- [x] 旧已加载代码在更新后不能宣称新制品身份；Reload 后才能重连。
- [x] 完成受影响 .NET/Browser/脚本回归及两轴 code review。
- [x] 真实 Chrome 验证开发加载、重连、离线与生产入口隔离以及更新后 storage 保留。
- [ ] Windows/Edge 与独立 Analytics 验收库中的真实活动到达验证。

## 验证边界

沿已确认设计采用 Browser discovery/HTTP、通用 ExternalHost handler、Runtime 启动前包选择和开发命令为测试入口。
不测试私有状态格式，不跳过 Package 校验。真实 Browser/平台/Analytics 结果单独记录，不以 handler 测试替代。

## Comments

2026-09-10：旧 Browser discovery 测试稳定复现：开发绑定缺失时实际返回生产默认端口 24820；修复后通过。
Profile handler、保留 Instance/config/Stream 更新、Desktop bootstrap、扩展输出切换等针对性测试通过。
`node scripts/browser-development.mjs --prepare-only --profile .local/browser-verification/desktop` 已通过。
默认 `.local/desktop` 已有运行者，默认 prepare 被目录锁拒绝，未停止该实例。

2026-09-10 closeout：

- 自动验证：全仓 .NET 1270 项、Browser 101 项通过；生产 Browser build、contract check、脚本测试及 shell 语法检查通过。
  最后补充的 bound discovery/Ready 投影已单独通过 handler 29 项回归。
- Standards review：已修正文档状态漂移并在 ADR-051 增加指向 ADR-057 的范围修订；未发现 Host 的具名 Browser 耦合或第二个 Runtime 权威。
- Spec review：已补齐独立浏览器数据目录、精确 Package/Ready 反馈和 ADR；复查通过，并修复停止监控后旧请求可能输出过期状态的问题。
- macOS 原生 Desktop：独立 Profile 启动到 native UI ready，选择正确精确包，旧路由返回 404，安装管理能力关闭，正常退出 0。
  证据：`.local/browser-verification/native-desktop-smoke-report.json`；同目录 `native-desktop-smoke.mjs` 可重复执行。
- 真实 Chrome：使用独立浏览器目录，启用开发者模式后加载本地扩展；真实页面活动进入绑定的 .NET ExternalHost handler，生产形态的 handler 无连接。
  在保持扩展已安装的情况下运行真实开发准备命令，得到不同 Package hash，再 Reload；扩展身份、本地状态保留，并成功连接更新后的精确包。
  绑定端口随后被无绑定服务占用时，请求只走绑定路由、outbox 留存、生产形态的 handler 仍无连接。
  证据：`.local/browser-verification/chrome-smoke-report.json`；同目录 `chrome-smoke.mjs` 可重复执行。
  此 Chrome 验证使用真实 Browser + .NET TestHost，原生 Desktop 启动另行验证，不等同于 Desktop 到 Analytics 的端到端验收。
- 开发命令：真实构建/准备/启动后达到 Desktop ready，SIGTERM 后正常退出 0 并释放命令锁。
  证据：`.local/browser-verification/command-smoke-report.json`；同目录 `command-smoke.mjs` 可重复执行。
- 剩余承接：维护者在 Windows/Edge 环境复验上述流程，并配置独立 Analytics 验收账号/库检查真实活动到达。
  当前环境未提供这两个验收条件，故 issue/PRD 为 ready-for-human；不关闭既有正式发布 gate。

2026-09-10 启动入口收口：按用户反馈，日常开发统一使用 `start-local.sh --browser chrome|edge` /
`start-local.ps1 -Browser chrome|edge`。Browser 参数隐含 Desktop 和源码监听，搭配
`--desktop-only` / `-DesktopOnly` 可复用既有开发栈；Node 保留为内部跨平台编排工具。
开发指南和 Browser README 已同步。Bash 语法及命令替身验证通过：Chrome/Edge 参数转发、
仅启动客户端时不调用 Docker、子进程退出码、原 Desktop 入口、非法参数、帮助及仓库外调用。
当前机器未安装 pwsh，PowerShell 分支未执行；其实际运行仍归入上面的跨平台验收 gate。


2026-09-10 项目参数最终统一：上述临时 `--browser` / `-Browser`、`--desktop-only` 已被项目选择语义替代。
现在 sh / pwsh 都用 `--collector browser`（自动补 Desktop），整套环境加 `--stack`，Edge 加 `--browser-app edge`。
详见 [本地启动 issue](../../local-development/issues/01-project-selection.md)。Desktop 已改为直连开发后端 18080；
现有开发栈若尚未发布此端口，先执行 `start-local --backend` 更新后端映射。
已用便携 PowerShell 在 macOS 上验证真实入口，并验证 IPC 正常停止 macOS Browser 编排与 Desktop；
Windows 原生 Console Ctrl+C、Windows/Edge 和独立 Analytics 到达继续保留为平台/账号验收 gate。
