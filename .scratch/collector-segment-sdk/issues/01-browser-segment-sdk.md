# Browser 接入最小 Segment SDK

Status: done

## 验收

- [x] startSegment、update、observe、end 与状态恢复接口接入 Browser 正式 fold。
- [x] fold/background 不再生成 FactId、计算 Segment 起点或处理轮转条件。
- [x] SDK 无浏览器对象规则和发送时钟；观测时间与输出内容由 Collector 提供。
- [x] 单份会话状态的原字段和协议快照形状保留，现有交付层负责 Revision/Stream/重试。
- [x] 现有回归、最小 SDK 接口测试和真实开发实例运行证据通过。

## 验证

改造前 Browser 106 tests passed。改造后：

- `npm run build --prefix collection/collectors/Heartbeat.Collector.Browser`：TypeScript 与 Vite 通过。
- `npm test --prefix collection/collectors/Heartbeat.Collector.Browser`：108 tests passed，14 files passed。
  原有行为断言保持；新增两个 SDK 接口场景覆盖读数更新、发送时钟独立、身份恢复、并行与轮转检查点。
- `node scripts/collector-contracts.mjs check`：通过，Package 内容引用一致。
- `git diff --check`：通过。
- 手写生产 TypeScript 净增加 45 行：SDK 73 行，fold 净减少 27 行，background 净减少 1 行。

开发 watcher 构建新包后，通用 discovery 确认当前精确 Package 有一个 Ready ExternalHost。
两个真实窗口在同一构建期间沿用各自 FactId/Start 延长 End，此前已排入队列的 SDK 快照出现
`delivered=true`，确认已有数据获 Analytics 上传确认。
脱敏证据：`.local/browser-sdk/runtime-evidence.json`，只记录包引用、连接数和 Fact 元数据。

完整扩展 Reload 实测创建新活动；这不是 SDK 状态恢复失败的自动复现，未据此重写 reload 机制。
已保存状态的恢复由 SDK/fold 回归验证；对 Reload 的文档表述已改为先保留旧快照，再重新对账。
当前开发栈继续运行。本轮没有新增人工门禁，也不推断 Windows/Edge 或 .NET 已做 SDK 验收。

## Comments

2026-09-10：用户同意从 Browser Segment 管理开始提炼 SDK，保持观测业务规则与现有 Facts 模型。
