# 02: 服务端 /segments 放行 system 源

Status: ready-for-agent

## Parent

[ADR-020](../../../docs/adr/020-system-collector-through-hub.md)

## What to build

删除服务端 `/segments` 入口对 `source='system'` 的拒收。按 ADR-020 的信任澄清,这条守卫只是皮带加背带:任何持 ApiKey 者本来就能经 `POST /usage` 写 system 轨,真正防冒充的是 Agent hub 侧守卫(issue 01)。放行后 Agent 可经 `/segments` 上传 system 段(issue 03 的服务端前提)。

## Acceptance criteria

- [ ] `POST /segments` 接受 `source='system'` 的段,经统一摄入例程入库
- [ ] 服务端测试:system 段经 `/segments` 入库后进入统计(Report 聚合可见)且快照 upsert 语义不变
- [ ] 插件段行为无回归(现有 segments 测试全部通过)

## Blocked by

None - can start immediately(与 01 并行)
