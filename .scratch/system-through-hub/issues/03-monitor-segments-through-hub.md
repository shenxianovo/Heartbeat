# 03: 主射弹 —— monitor 产段经 hub 直达 /segments

Status: ready-for-agent

## Parent

[ADR-020](../../../docs/adr/020-system-collector-through-hub.md)

## What to build

system 采集器成为经由 hub 的一等 Collector。monitor 直接产出插件段同构的 ActivitySegment 快照(客户端算 system IdentityKey;away 段同构:`source='system'`、AppName 哨兵、稳定 Id 快照生长),经新 seam **ISegmentSink**(hub 是唯一生产 adapter)以推模型进入 hub 缓冲:**闭合即推 + 进行中段 30s 定时快照**(常量,暂不进配置)。上行走 `/segments`,客户端 usage 管道整条退役。

连带(同一原子动作,拆不开):

- 客户端 usage 上传 service、usage 离线缓存、拉模型 drain 接口、`AppUsageItem` 装配全部删除;旧 cache.json 升级时孤儿化(留日志),不写迁移代码
- 图标上传挂点从 usage 批次迁到段批次(drain 后 distinct AppName 触发,行为与今天一致)
- **托管服务注册顺序翻转**:worker 先注册、monitor 后注册,使 StopAsync(逆序)先停 monitor(终态快照推入 hub)再停 worker(最终 drain + 上传)——现状顺序在推模型下每次关机丢尾巴;顺序依赖用注释钉住
- 状态机测试(17 个)观察点从拉模型 drain 迁到 fake sink,断言推出的段;行为覆盖不减,并新增"StopAsync 推出终态快照"测试

## Acceptance criteria

- [ ] 前台切换/标题门控/away 进出/AwayProcessNames 归一化行为与迁移前一致(既有测试全数迁移通过)
- [ ] 段闭合即刻出现在 hub 缓冲;进行中段每 30s 一份快照(同 Id,EndTime 单调生长)
- [ ] monitor StopAsync 时终态快照进入 hub,且 worker 最终 drain 能带走它(注册顺序测试或注释钉住)
- [ ] system 段与插件段经同一缓冲、同一缓存、同一上传路径出网到 `/segments`
- [ ] 端到端验证:真机跑 Agent,服务端 ActivitySegment 表出现 system 段,Dashboard 排行/时间轴正常
- [ ] `AppUsageItem`/usage 缓存/usage 上传 service 在 desktop 侧无引用残留

## Blocked by

- [01](./01-guard-to-loopback-testable-ingest.md)
- [02](./02-server-segments-accept-system.md)
