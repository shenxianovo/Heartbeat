# Browser Collector

Chrome MV3 ExternalHost Collector。它观察活动标签页，通过 loopback Collector Protocol 把
Browser Segment Fact 交给本机 Agent；不持有服务端凭据或地址。

## 目录

- `src/background.ts`：composition root。
- `src/delivery.ts`：持久交付深模块；`src/delivery-chrome.ts` 提供 Chrome adapter。
- `src/protocol.ts`：ExternalHost HTTP binding。
- `src/fold.ts`、`src/normalize.ts`：Segment fold 与 URL identity。
- `Package/`：manifest、observation declaration 与 artifact staging 来源。

未 ACK outbox 不丢弃；容量溢出折叠为持久 Stream Gap。

## 构建、验证与加载

```bash
npm --prefix collection/collectors/Heartbeat.Collector.Browser test
npm --prefix collection/collectors/Heartbeat.Collector.Browser run build
node scripts/collector-contracts.mjs stage browser .local/browser-package
```

在 Chromium 扩展页开启开发者模式，加载 `.local/browser-package/browser-extension/`，并启动
Desktop Agent。当前 Package 随 Desktop release 交付；Package 托管与下载尚未实现。

术语见 [Collection Context](../../CONTEXT.md)，行为契约见
[Conformance Suite](../../protocol/conformance/README.md)，Fact payload 见 [Contracts](../../contracts/README.md)。
