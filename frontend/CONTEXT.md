# Dashboard

可视化使用数据。只读消费 Analytics API，不产生数据（唯一例外：登录态）。

## Language

**Dashboard**:
主页面：状态卡、今日排行、周图表、时间轴、键盘热力图的组合。30s 轮询报表；`useHeartbeat` 是瘦协调器，组合设备选择 / 在场 / 报表三个数据域。

**Timeline（时间轴）**:
按天回放的注意力线（ADR-019 主视图）：单一时间线跟随 system 前台段。simple / detailed 双模式，detailed 可拖拽缩放、看标题明细。
_Avoid_: Replay 泳道、多轨（那是 ADR-019 的展开态，未建）

**Label Upgrade（标签升级）**:
ADR-019 §2：渲染 system 段时若存在同 App 的重叠插件段，标签由窗口标题升级为插件语义（URL/页面）。按时间窗口判定，无覆盖的时段（含插件安装前全部历史）fallback 到 Title Formatter。纯展示层，不改数据。

**Title Formatter（标题归一化）**:
ADR-016 的展示层无损归一化：per-app formatter 把原始窗口标题洗成友好显示（去应用名后缀、去 tab 计数后缀、spinner 归并等）。是 Label Upgrade 缺席时的兜底层。
_Avoid_: 在采集端或服务端做标题清洗（无损原则，展示层是唯一动标题的地方）

**Presence（在场）**:
设备在线状态与当前前台应用（`useDeviceStatus` + CurrentAppPanel）：Dashboard 里唯一的"现在时"信息，其余都是回顾。

**Away（离开）**:
ADR-014 的离开段在展示层的形态：特殊 App 标签（`AWAY_APP`），时间轴上显示为离开区间，排行与统计中单列，不与真实应用混排。

**Keyboard Heatmap（键盘热力图）**:
InputEvent 的可视化：按键频次热力图。只消费聚合频次，不展示按键序列（原始序列等价于键盘记录，永不上屏）。
