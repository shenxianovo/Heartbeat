# 当天经历首版

Status: done

## 范围

新增 `/u/:username/experience`，保留原 Dashboard；首版读取 Segment 活动，键鼠 Event 聚合
留在原 Dashboard，Measurement 等待真实需求。来源专用呈现由前端 TypeScript Fact View 提供。

## 验收

- [x] 原看板有新入口；新页面可回原看板。
- [x] 原生 Segment 分页读取，不依赖 activityKey、Device 或 AppIdentity 必填。
- [x] 使用现有 Owner 可见性门及严格本地日历窗口，支持 DST 日长；无新增迁移。
- [x] Subject 泳道按所选范围内活动显示，不读取当前在线状态作为过滤条件。
- [x] 顶部全天总览框选、移动选区、拖动边界；泳道滚轮缩放、拖动平移、Shift 框选、方向键；不合并 Segment。
- [x] Browser 默认可展开；System 详情保留同 Subject/AppIdentity/时间重叠的所有候选。
- [x] VRChat Account 独立显示世界和原始区间，缺段不标离线。
- [x] 未支持的 Segment 来源有记录数与最多 3 条收起的 Payload 样例；详情保留身份、修订、时间。
- [x] 逐条阅读分页；切日期取消旧请求；网络中断明确提示结果不完整。
- [x] 自动检查及本地真实数据核对完成，记录证据。

## 实现边界

- API 每页最多 500 条，以稳定行 Id 游标继续；前端仅加载所选一天，逐页提示进度。
  当前日不是跨请求数据库快照，刷新重新读取最新修订；不声称持续实时更新。
- Canvas 在整天尺度保留逐条短记录标记，原始边界、身份、Payload 可逐条查看。
  触控通过总览选区两侧边界缩放，支持平移；泳道精确框选通过 Shift + 拖动。
- 无新的 Source/schema 注册、动态 Collector UI、BI 编辑器或输入原始事件序列。

## 验证

- `dotnet test server/Heartbeat.Server.Tests/Heartbeat.Server.Tests.csproj --no-restore --configuration Release`：529 通过。
  其中新查询覆盖 Account/任意 Payload/无 App、Owner 隔离、500 条游标分页、相同开始时间、
  半开窗口、合法零长度快照；HTTP 检查沿用 DST 日历契约及 public/private 可见性门。
- `cd frontend && npm run verify`：类型检查、282 个测试及生产构建通过。
  新测试覆盖关联多候选、未知 Payload、不安全 URL、日期请求竞争、分页中断提示、视图/聚焦。
- 本地 Compose 仅重建 Analytics/Frontend，`/health` 200，四个服务均 running；无新增迁移。
- 本地 `shenxianovo`，2026-09-09 / Asia/Shanghai：API 441 条（System 419、Browser 22），
  与 SQL 按同一 Owner/窗口计数完全一致；2 个 Subject，一页读取测得 85ms（本地单次观测）。
- 隔离 Chrome：真实页面拖动平移、Shift 框选、视图切换、390px 窄屏无横向溢出，无页面 JS 错误。
  确定性示例额外核对 VRChat、未知来源、Browser 重叠与 Ctrl 滚轮缩放。
- 检查材料在 `.local/verification/daily-experience/`（不提交；包含本地数据截图）：
  `live-check.mjs` / `visual-check.mjs` 与桌面、聚焦、窄屏截图。
  实际观察后完善了“离开”显示、跨日时间标注与秒级刻度。
- 未部署生产，未更换 Vue；视觉可用性仍由 Owner 在首版基础上继续反馈。

## 首版反馈

2026-09-09：Owner 认可核心交互，要求去掉无意义的解释说明，并希望用生图设计稿选择视觉方向。
已删除副标题、段边界解释、关联规则解释、排序说明与原看板统计提示；手势帮助默认收起。
保留空态、错误、未完成加载提示及可见范围/时区等必要信息。生成的三种视觉方向仅用于挑选样式，
图片中自行添加的标语、世界缩略图或未经确认的关联不构成实现需求。

Owner 随后将本轮视觉迭代收敛到上方组件，采用三张稿的共同布局。组件命名“活动泳道”
（`ActivitySwimlanes.vue`）：上方全天总览，下方共享时间刻度与 Subject 泳道。
删除“全局／聚焦”和加减四个按钮及旧单值滑块；总览支持框选、拖动选区平移、拖两侧边界缩放、
双击还原全天和键盘操作。阅读模式或所选范围无活动时，总览仍可操作。保留原生短记录，
没有照搬旧主页 `mergeActivityBursts` 的绘制数据。下方记录/详情沿用首版，未做本轮设计重排。

本轮验证：前端类型检查、285 个测试、生产构建通过；真实浏览器检查总览框选、独立调整边界、
移动选区保持跨度、双击恢复、阅读模式保留总览，以及 390px 窄屏无溢出。检查脚本为
`.local/verification/daily-experience/swimlanes-check.mjs`，可用 `CHECK_ORIGIN=http://127.0.0.1:8080`
检查本地 Compose；本轮仅重建 Frontend。

Owner 最新反馈：删除整个“操作”帮助与“泳道／逐条阅读”切换，泳道与下方记录列表始终同时显示。
已移除相应模式状态、组件开关和样式；保留总览交互及记录选择。

Owner 确认“设备总览＋应用展开”：保留多 Subject 汇总泳道，Machine 可独立展开为主页式应用图标、
名称和时间段行。应用按 Subject + AppId 分组，只分行、不合并任何原始 Segment；没有产品映射时
以 AppIdentity/Stream 保留独立行。Account 与 Browser 的既有展示规则保留。

本轮共用主页 `useTimelineDrag` 的方向锁定、横向平移与纵向滚动；支持整块时间线区域拖动，
滚轮以指针所在时间缩放。共享逻辑允许泳道传入实际轨道宽度及 1 秒最小视窗，旧主页仍保留
5 分钟最小值。拖动后的点击不会选中记录，设备汇总行随应用列表滚动保持可见。
初始范围沿用主页：当天围绕现在、历史日围绕最早活动的 2 小时（按真实日历日边界裁切）；
分页完成后才确定历史首条，不因后续刷新覆盖用户已探索的范围。总览双击仍恢复全天。
后端 raw read DTO 补充 nullable AppId，供已有授权图标接口使用；无数据库或迁移变更。

验证：前端 287 个测试、类型检查及生产构建通过，后端 Experience/日历 HTTP 相关 9 个测试通过。
本地 Chrome 核对两个设备独立展开、应用图标、标尺拖动平移、滚轮缩放、刷新保留视窗、
应用列表纵向拖动、拖动结束不选中记录、390px 无横向溢出；无页面 JS 异常。
可重复检查入口：`.local/verification/daily-experience/device-expansion-check.mjs`；
旧 `swimlanes-check.mjs` 已调整为先还原全天再检查总览框选，不再假定首屏是全天。
本地 Analytics/Frontend 重建，生产未部署。阅读体验继续由 Owner 在当前预览路由反馈。

2026-09-09 存档反馈：Owner 对当前设备展开与泳道体验不满意，要求先提交工作区存档。
本提交保存已实现且通过验证的预览版本，不代表视觉方案已获认可；暂停继续调整，后续方向待讨论。
