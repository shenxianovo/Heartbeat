# 当天经历回放验收

日期：2026-09-15

基线：`cleanroom-rewrite` 的 `bf2169b`

本轮恢复 main 的“当天经历”交互，但沿用当前四层记录模型和 React 前端，不带回旧对象关系。

## 已确认

- 全天概览支持拖选、移动和调整范围；泳道支持平移、缩放、键盘操作和日期导航。
- 数据按 Collector、Track 和可选协议子组展示。应用按平台原生身份分组，不按显示名合并。
- Range Track 全天读取后在浏览器裁剪；Point Track 使用服务端计数，选择局部桶后才分页读取明细。
- 当前视窗无记录的分组会隐藏，切换日期或来源后不会保留已排除记录的详情。
- 重叠区间分行显示，半开窗口边界不会留下伪造的零宽 Range；同一时刻的 Point 仍保留。
- 未知协议显示时间位置和安全的原始 JSON。公共布局不解释业务 value。
- 窄屏没有页面级横向溢出，时间刻度按绘图区宽度调整。

本轮未引入旧 Object/Fact 关系、跨来源 Device Identity、浏览器观察或应用图标服务。

## 验证

以下检查在 2026-09-15 通过：

```bash
npm run verify
npm run test:e2e
```

Chromium 场景覆盖范围选择、来源组合、记录聚焦、重叠区间、Point 密度、未知 JSON、失败重试、键盘操作和窄屏布局。

浏览器测试模拟认证和记录 API，只证明前端读取契约与交互，不证明真实 Collector 到 Web 链路。实际命令、运行结果和截图以对应 `.artifacts/verification/` 运行目录为准。

## 2026-09-27：全局概览与领域下钻

首页 `/` 改为按来源展示的时长气泡；VRChat 气泡进入 `/vrchat`，桌面来源及「详细时间线」进入 `/timeline`。下钻传递时间范围与来源，领域详情和时间线提供返回概览入口；页头不再放置 VRChat 按钮。具体口径见[前端契约](../../src/Frontend/Heartbeat.Web/README.md#全局概览与下钻)。

本次通过：

- `dotnet run --project tools/Heartbeat.Dev -- verify closeout --base ce0a1e79a8832311366a94f8ac1ad9a7ebdf52ac`：构建、现有回归、前端检查、浏览器回归和结构质量闸门均通过。证据：`.artifacts/verification/20260927T055553Z-verify-closeout-df7841ae6a5b41f9ba512d8ecbd0d55c/`。
- `dotnet run --project tools/Heartbeat.Dev -- scenario replay-fixture`：概览到领域/泳道、来源与时间范围传递、空目录、失败恢复、未知来源入口及窄屏场景通过。已查看桌面和手机截图。证据：`.artifacts/verification/20260927T055232Z-scenario-replay-fixture-69c49d14fafb4136a5aaa0d1d616e34f/`。

认证回归另覆盖需要登录时保留详情筛选。上述浏览器数据为 fixture，未部署，未重新运行原生桌面场景，也不证明真实 VRChat 事件覆盖。
