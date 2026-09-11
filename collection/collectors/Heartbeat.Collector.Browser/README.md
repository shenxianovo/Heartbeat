# Browser Collector

Chrome/Edge MV3 ExternalHost Collector。它观察各窗口的活动标签页，通过通用 loopback Collector Protocol
把 Segment Fact 交给 Desktop；不持有服务端凭据或地址。System 仍由 Desktop 单独内置。

开发迭代使用仓库根目录的 `./scripts/start-local.sh --collector browser`（macOS）或
`./scripts/start-local.ps1 --collector browser`（PowerShell）；需要三件套时加 `--stack`，Edge 加 `--browser-app edge`。
入口内部调用 Node 编排构建和源码监听，
通过具体 Desktop Profile 的绑定连接；更新保留身份、配置和 outbox，开发扩展约 30 秒内检测新构建并自动 Reload。
已有旧版扩展需要先手动 Reload 一次，让自动更新逻辑生效。
首次加载与独立目录步骤见 [开发指南](../../../docs/development.md#browser-开发与更新)。
普通生产构建仍按端口发现，不应拿它冒充开发扩展。

## 观测模型

`window-activity.ts` 判定一个窗口内的页面活动：规范化 URL 相同则更新读数，变化则开始新活动，
窗口关闭则结束观测。`fold.ts` 将变化映射为 `sdk/segments.ts` 的 Segment 操作，SDK 管理身份、起点和超长段轮转；
`background.ts` 负责 Chrome 回调、持久检查点、对账及交付接线。

FOI 是 App 产品；窗口只是并行活动的会话分组，URL/标题是读数。并行状态仍只保留在 `FoldState.open[windowId]`，
不增加对象登记或第二份窗口表；关闭后移除运行状态，已输出的 Fact 保留。fold 与待发快照写入
同一个持久 journal，快照内容变化使用单调 Revision；Service Worker 重启延续同一事实。
整个浏览器重启时按最后已知快照收尾，当前窗口从当前观测重开，避免扩张停机区间。
旧 key 迁移、已 ACK 高水位恢复与真实 Profile 安装验收见 [Browser 缓存兼容](cache-compatibility.md)。

验证重点是两个窗口同时记录、关闭其一不影响另一个、重新打开产生新 Fact，以及同页更新延续旧 Fact。

最小 Segment SDK 目前位于 Browser 包内，没有独立发布。接入示例与职责见 [SDK 说明](src/sdk/README.md)。

## 构建与验证

需要 Node.js 24、.NET SDK 10 和 Python 3。Browser 的测试拥有一个独立 .NET TestHost，用真实
`ExternalHostCollectorProtocolHandler` 验证 TypeScript 客户端；生产 Host 不引用 Browser 或这个 fixture。

```bash
npm ci --prefix collection/collectors/Heartbeat.Collector.Browser
npm --prefix collection/collectors/Heartbeat.Collector.Browser run build
npm --prefix collection/collectors/Heartbeat.Collector.Browser test
node scripts/collector-contracts.mjs check
node scripts/collector-contracts.mjs stage browser .local/browser-package --version 0.1.0
python3 scripts/package-browser-release.py --package .local/browser-package --version 0.1.0 --output .local/browser-release
```

build 同步更新已跟踪的 `Package/browser-extension/`。stage 生成 observation/artifact hash 与
`collector-artifact-ref.json` 的精确 Package/Artifact 身份；引用文件由最终 manifest 派生，不进入 artifact
payload hash，避免循环依赖。它不是授权凭据，Host 按自己的 Installation 逐字段核验。

首版只支持 Windows/macOS 上的 Chrome 与 Edge。品牌来自
[User-Agent Client Hints](https://learn.microsoft.com/en-us/microsoft-edge/web-platform/user-agent-guidance)，
平台来自 [chrome.runtime.getPlatformInfo](https://developer.chrome.com/docs/extensions/reference/api/runtime#method-getPlatformInfo)。
未知或冲突品牌不发起 Activation，不根据通用 Chromium UA 猜测 Chrome。

## 独立交付与使用边界

`release-collector-browser.yml` 只响应 `collector-browser/vX.Y.Z` tag，并再次校验严格稳定版本格式。
一份确定性 zip 登记到 Windows/macOS × x64/arm64 四个 target；同版本不同字节拒绝覆盖，旧版本不回退
Catalog Latest。发布先追加精确静态目录，公网逐字节回读成功后才在共享锁内更新自己的 Catalog 条目。
发布 Browser 不构建、发布或重启 Desktop、Headless、Frontend、Analytics。

Desktop Marketplace 安装后创建一个 Machine-scoped Instance，无外部连接时显示“等待连接”。操作者自行从
Installation 的 `browser-extension/` 手工 Load unpacked；Desktop 不代替浏览器加载扩展。多个浏览器/Profile
共享一个 Instance，各自持久化 External Host Identity；Service Worker 重启建立新 Activation，正常重连保留
Stream。未 ACK 的 Facts/Stream Gaps 留在本地 durable outbox。

完整卸载撤销全部 lease；仍加载的扩展重连得到 `package_not_installed`，不会重建已删除的 Instance。
生产 Marketplace 没有 Store 分发、自动扩展 reload 或 Package 更新；其新版本需先卸载再安装。
独立开发 Profile 的启动前更新见 [ADR-057](../../../docs/adr/057-development-external-host-profile-binding.md)。

发布记录及 Chrome/Edge 加载、采集、Backend 到达与卸载的剩余实机验收统一见
[issue 07](../../../.scratch/collector-package-registry/issues/07-deploy-and-vrchat-smoke.md)。

术语见 [Collection Context](../../CONTEXT.md)，行为契约见
[Conformance Suite](../../protocol/conformance/README.md)，Fact payload 见 [Contracts](../../contracts/README.md)。
