# Context Map

Heartbeat 是一个以桌面活动为核心的个人数字活动档案与回放系统。系统分为三个领域上下文
和一个共享内核。

## Positioning

**单用户自部署的个人系统，商业化是被保留的选项而非被服务的目标。** 当前唯一用户是 owner 本人；采集深度（InputEvent、loopback 不鉴权）以"用户 == 数据主人 == 部署者"为前提合法。通往多用户/消费级的门通过三条不变量保持敞开，但不为其投入当下成本：

1. 每个依赖单用户前提的决定显式写进 ADR trade-off（ADR-012、ADR-017 已如此）
2. 数据模型不焊死多租户（User/Device/OwnerId 隔离已存在）
3. 采集能力分层可拆（ADR-017 pluggable collectors）——浅信任场景只装浅层采集器

**采集边界随需求生长，不预建。** 愿景（"x年前的今天我在做什么"）的产品承诺是如实回放**数字活动**；非 PC 数据源（手机、日记、照片）在 ActivitySegment/Source 模型下天然可接入，但只在某类空白真实造成困扰时才建对应采集器。当前阶段聚焦把 PC 采集做深。

## Contexts

| Context | Directory | Responsibility |
|---------|-----------|----------------|
| Collection | `collection/` | 监听前台窗口切换与各应用内活动，生成使用记录，上传至服务端。`hub/` 是可复用的 Collector Runtime，**多实例星形直连 Analytics、不嵌套**（ADR-032）；`desktop/` 是桌面实例（含 system Collector 与通用 ExternalHost loopback 监听，不认识任何具名可选 Collector，ADR-049）；`collectors/` 存放 Browser、VRChat 等独立 Collector；`contracts/facts/` 是 Fact Schema 唯一权威来源。server 旁的无头 host 以一个 Runtime 托管多个账号级 Instance，不携带桌面概念。 |
| Analytics | `server/` | 严格接收版本化事实快照，持久化活动数据，生成报表与叙事知识投影 |
| Dashboard | `frontend/` | 可视化使用数据 |

## Shared Kernel

`shared/Heartbeat.Core` — Collection 与 Analytics 共享的 DTO、协议常量和纯验证/压缩规则。

## Relationships

```
Collection ──uploads──▶ Analytics ──serves──▶ Dashboard
     │                      │
     └──── Shared Kernel ───┘
```

- Collection → Analytics: 上游/下游（Upstream/Downstream），Collection 生产数据，Analytics 消费并持久化
- Analytics → Dashboard: 上游/下游，Analytics 提供 API，Dashboard 消费。**读为主**：使用数据只读呈现。**一处写例外**——叙事知识层（ADR-028/029/031）：Dashboard 把用户确认的 Strand 树、Episode、Matcher/Mute、RecurrenceProbe 与教学变更 POST 回 Analytics；知识库、发问/整理 LLM 调用和 Recap 知识投影均归 Analytics 所有。
- Dashboard → Collection Hub Management Surface: owner 浏览器通过同源路由直接读取 Subject 授权状态并提交一次性交互应答；Analytics 不代理第三方账号凭据、Collector Secret 或管理命令（ADR-043）。
