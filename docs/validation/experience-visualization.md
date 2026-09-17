# 当天经历可视化恢复

日期：2026-09-15。基线：`cleanroom-rewrite` 的 `bf2169b`。

用户确认恢复 main 的「当天经历 / 活动泳道」，并确认第一版按 Collector 采集来源分组，内部展示 Track、可展开应用子泳道。参考固定 main `86911e7` 的 `ExperienceView.vue`、`ActivitySwimlanes.vue`、`ActivityOverview.vue`、`FactLane.vue` 与时间交互模型。

后续按用户反馈保留「当天经历」组织方式，视觉改为同一 main 基线的原有 `ActivityTimeline.vue`：紧凑刻度、灰底标签列、细活动条与小型概览。按钮、弹出日历和 Picker 参考 main 首页 `Dashboard.vue` 及共享控件，并提取为 React 的 `Button`、`Popover`、`DatePicker`、`MultiSelectPicker`、`Icon`。

## 已实现

- 全天概览、拖选与调整范围、泳道平移缩放、记录聚焦、日期导航、来源多选和记录详情联动；窄屏及键盘操作可用。
- 来源 → Track → 可选协议子组。应用子组使用平台、标识种类及应用 ID，不按显示名合并。分组信息通过同一 presentation registry 提供，公共布局不解释业务 JSON。
- 全天 Range 完整读取，视窗在本地裁剪；Point 全天概览与局部密度查询分开。视窗停稳后按当前范围及更细粒度读取，保留局部原始明细分页。没有新增后端协议、存储分桶或全天输入明细加载。
- 旧记录不被合并成新数据；未知协议保留时间/原始 JSON；能力状态、重叠原因与观测空白保持现有含义。
- 当前视窗内没有记录的应用子泳道、父 Track 和 Collector 分组逐级隐藏，并保留范围切换前的展开状态。UI 使用“前台应用”，避免把系统观测描述为人的实际活动。
- 每条 Track 在时间线中独立展示，不跨 Track 投影或合并。观测状态在当前视窗有记录时显示自己的泳道并进入区间记录列表。

本轮没有引入 main 的旧对象关系、跨来源设备关联、Browser 相关观察或图标服务。应用图标使用文字占位；后续真实身份与资源接入需另外明确。

## 验证结果

`npm run verify` 通过：类型检查、ESLint、Prettier、Vitest 和生产构建。`npm run test:e2e` 的 Chromium 用例全绿，覆盖：

- 概览拖选和手柄调整同步时间范围，Point 查询携带正确范围及更细粒度，同时不重复读取全天 Range。
- 应用子泳道展开、记录选择与聚焦、切换日期后清理详情。
- 当前范围过滤应用子泳道、父 Track 和 Collector；观测状态作为独立 Track 展示并可点击查看详情。
- 拖动平移保持跨度、滚轮缩放、键盘端点操作与日期边界。
- 重叠区间可直接点击；未知 JSON 安全展示；来源组合和空选择；失败重试及退出清理。
- 窄屏无页面横向溢出，时间刻度不重叠。
- 日期日历的月份切换、方向键选日、今天、Escape 关闭与焦点恢复；多选 Picker 保持选择、外部点击关闭和窄屏定位。

新增回归还覆盖：恰好在视窗起点结束的区间不得残留成零宽标记，同一时刻的真实 Point 仍保留。该问题先用最小测试复现失败，再统一到半开窗口判断。截图检查发现固定刻度数在窄屏上拥挤，已按绘图区宽度生成刻度，并增加浏览器几何断言。

浏览器测试模拟认证和记录 API，验证读取契约及页面交互，不等于真实采集到 Web 的联调。此次仅修改前端及文档；仓库 `verify changed --base HEAD` 因文档路径扩大为 full fallback，同时重跑并通过完整 .NET 解决方案测试。截图可由 `tests/e2e/replay.spec.ts` 重现到 `test-results/experience-expanded.png`、`replay-observation-track.png`、`replay-mobile.png` 等文件。

本次另检查 `main-calendar.png` 和 `main-picker-mobile.png`；窄屏泳道标签使用截断及完整标题提示，避免紧凑标签列内换行。登录回调测试改为定位可点击活动条，保持对回放加载成功的验证，不依赖已去除的活动条内嵌文字。
