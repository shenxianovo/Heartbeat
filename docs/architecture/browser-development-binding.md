# Browser 开发泳道接入与更新

状态：2026-09-10，连接绑定与保留状态的开发更新已实现；自动回归、独立 macOS Desktop 启动及真实 Chrome 更新验证已通过。
Windows/Edge 和 Analytics 真实到达仍待验收，不能从协议测试推定全部完成。
决定见 [ADR-057](../adr/057-development-external-host-profile-binding.md)，操作入口见
[开发指南](../development.md#browser-开发与更新)，状态与验收见
[issue 01](../../.scratch/browser-development/issues/01-profile-binding-and-update.md)。

## 已实现的连接边界

- `--development` 必须显式选择并独占独立 Desktop Profile，拒绝默认生产目录及其别名。
- Profile 的 `external-host-binding.json` 持久保存身份、随机连接凭据和固定端口。端口被占只等待，
  不扫描生产范围；默认生产启动不启用开发接入能力。
- 通用 ExternalHost handler 在处理每个开发请求前验证 Profile 专属路径，拒绝旧无绑定入口。
  生产 handler 不接受开发路由。开发 discovery 还提供精确 Instance 包引用及真实 Ready 连接数。
- 开发扩展由构建模式固定，不接受通过 options/storage 切成生产模式。缺少绑定、错误 Profile、旧端口、
  不兼容路由及 HTTP 重定向均不能回退生产；本地 storage 固定首次绑定的 Profile，防止带着 outbox 改连。
- 文件更新后，旧 Service Worker 的构建标识与本地连接文件不符时停止交付，等待 Reload（开发扩展自动检测新构建）；
  不能让旧已加载代码使用新文件的 Package 引用冒充新制品。

保证是不向错误 Profile 建立采集会话或交付事实；端口被其他进程复用后，一次失败的 TCP 探测仍可能
到达该进程。这不是针对控制本机账户的恶意进程的安全隔离。

## 已实现的开发更新

统一启动入口为 `start-local.sh --collector browser` / `start-local.ps1 --collector browser`，
需要三件套时加 `--stack`，选择 Edge 时加 `--browser-app edge`。入口内部调用 `browser-development.mjs`，
从源码构建独立开发扩展，stage 并计算完整 Package/artifact hash；输出不覆盖
已跟踪的生产 payload。构建失败保留旧 Desktop 与扩展。成功后正常停止本命令拥有的 Desktop，
通过真实目录锁和通用 Installation/Runtime 在首次 Activation 前选择新包，随后启动 Desktop。
已有其他运行者占用该 Profile 时拒绝更新，不停止它，也不删除锁文件。

Instance、配置、Stream、Secret 和未确认交付继续由原 Runtime 拥有；旧不可变 Installation 不覆写。
扩展使用固定输出目录与公开 manifest key 保持身份，目录交换中断后先恢复并检查旧绑定。
`--browser-app chrome|edge` 提供各自固定的独立浏览器数据目录；浏览器数据不随命令退出删除。
首次 Load unpacked；已有旧开发扩展手动 Reload 一次后，后续更新自动检测并 Reload，
避免卸载重装清除身份和未确认记录。开发闹钟约 30 秒检查一次本地构建标识，只接受已固定的同一
Profile，在串行事件队列中先持久化当前活动，再调用官方 `chrome.runtime.reload()`；持久化失败
不 Reload，同一个构建只自动尝试一次，避免失败循环。生产扩展不注册此闹钟。

扩展连接凭据只加入本地加载目录，不写入公开 Package；开发构建标识与绑定文件相互校验。
工具打印精确包版本/hash，持续区分 Desktop 离线、包不匹配、等待扩展和至少一个连接 Ready。
Ready 不等于 Analytics 已接收事实。首次使用的开发 Profile 仍需配置自己的 API key。

采用短暂离线的启动前更新，复用既有 Runtime 权威；不建设在线候选/LKG、生产自动更新或 Package
脚本执行器。用户正在进行的观测模型、SDK、数据库及插件市场设计不因本次接入改变。

## 验证证据与限制

- Browser discovery 最小回归先红后绿：缺少开发绑定时，旧代码实际返回生产默认端口 24820。
- handler 回归覆盖每个操作的旧路由/错误绑定拒绝、精确包与 Ready 投影；Runtime 回归覆盖启动后拒绝
  更新，以及重启更新后的 Instance/config/Stream 连续性。
- 全仓 .NET 1270 项、Browser 101 项测试通过；新增脚本测试验证更新、改绑拒绝、身份保持及中断恢复。
- 独立 macOS Profile 的 native UI ready、精确包选择、旧路由拒绝、安装管理能力关闭与正常退出 0 已验证，
  报告位于 `.local/browser-verification/native-desktop-smoke-report.json`，可重复脚本在同目录。
- 常用 `.local/desktop` 已有运行者；默认准备命令被锁拒绝，未停止该实例。实际准备/更新验证使用
  `.local/browser-verification/desktop`，不代表当前日常开发实例已经切到新代码。
- 真实 Chrome 在独立浏览器目录中完成页面采集、精确包替换后 Reload 重连及身份/storage 保留；
  端口复用时只访问绑定路由、保留 outbox，生产形态的 .NET TestHost 无连接。报告与可重复脚本在
  `.local/browser-verification/chrome-smoke-report.json`、`chrome-smoke.mjs`。此项使用真实 Browser +
  .NET TestHost；Windows/Edge、真实 Desktop → 独立 Analytics 验收库仍待验证。已有 Browser 发布的[实机 gate](../../.scratch/collector-package-registry/issues/07-deploy-and-vrchat-smoke.md)
  不由本轮关闭。

Chrome 开发身份使用公开 manifest key 的依据见 [Chrome 文档](https://developer.chrome.com/docs/extensions/reference/manifest/key)。

自动重载采用 [Chrome runtime.reload](https://developer.chrome.com/docs/extensions/reference/api/runtime)；
开发闹钟遵循 [MV3 Service Worker 生命周期](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle)。

2026-09-10 自动 Reload 实机验证：真实 Chrome 在换包后约 26 秒自行重载，身份/storage 保留且新包重连；
报告在 `.local/browser-verification/chrome-auto-reload-report.json`，状态见 [issue 02](../../.scratch/browser-development/issues/02-automatic-reload.md)。
