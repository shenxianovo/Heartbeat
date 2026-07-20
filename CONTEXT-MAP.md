# Context Map

Heartbeat 是一个 Windows PC 应用使用时长监控系统。系统分为三个领域上下文和一个共享内核。

## Positioning

**单用户自部署的个人系统，商业化是被保留的选项而非被服务的目标。** 当前唯一用户是 owner 本人；采集深度（InputEvent、loopback 不鉴权）以"用户 == 数据主人 == 部署者"为前提合法。通往多用户/消费级的门通过三条不变量保持敞开，但不为其投入当下成本：

1. 每个依赖单用户前提的决定显式写进 ADR trade-off（ADR-012、ADR-017 已如此）
2. 数据模型不焊死多租户（User/Device/OwnerId 隔离已存在）
3. 采集能力分层可拆（ADR-017 pluggable collectors）——浅信任场景只装浅层采集器

**采集边界随需求生长，不预建。** 愿景（"x年前的今天我在做什么"）的产品承诺是如实回放**数字活动**；非 PC 数据源（手机、日记、照片）在 ActivitySegment/Source 模型下天然可接入，但只在某类空白真实造成困扰时才建对应采集器。当前阶段聚焦把 PC 采集做深。

## Contexts

| Context | Directory | Responsibility |
|---------|-----------|----------------|
| Collection | `desktop/`, `collectors/` | 监听前台窗口切换与各应用内活动，生成使用记录，上传至服务端。`desktop/` 为 ingest hub（Agent，含 system 采集器）；`collectors/` 存放各应用内采集器（browser 已落地，vscode 等规划中），经 loopback 汇入 hub（ADR-017） |
| Analytics | `server/` | 接收使用数据，合并碎片记录，聚合报表 |
| Dashboard | `frontend/` | 可视化使用数据 |

## Shared Kernel

`shared/Heartbeat.Core` — 跨上下文共享的 DTO 和核心工具（如 UsageMerger）。

## Relationships

```
Collection ──uploads──▶ Analytics ──serves──▶ Dashboard
     │                      │
     └──── Shared Kernel ───┘
```

- Collection → Analytics: 上游/下游（Upstream/Downstream），Collection 生产数据，Analytics 消费并持久化
- Analytics → Dashboard: 上游/下游，Analytics 提供 API，Dashboard 消费。**读为主**：使用数据只读呈现。**一处写例外**——Strand 知识层（ADR-028/029）：Dashboard 把用户对 Matcher 的裁决（绑定 Strand / Mute）POST 回 Analytics，知识库归 Analytics 所有（发问 LLM 调用与投影都在服务端）
