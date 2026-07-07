# 04: 服务端删 POST /usage 映射层

Status: ready-for-agent

## Parent

[ADR-020](../../../docs/adr/020-system-collector-through-hub.md)

## What to build

issue 03 落地后 Agent 不再调 `POST /usage`,同 PR 原则(单用户自更新舰队 lockstep,ADR-018 先例)删除服务端整条映射层:上传入口、usage→segment 映射方法、上传 DTO、usage 校验策略及其测试。**GET /usage 查询投影保留**——它是 Dashboard Timeline 的 `usageData` 来源(公开 by-username 镜像同理),死的只是上传路径。段校验由 SegmentValidationPolicy 独家负责,消灭双校验策略分叉。

## Acceptance criteria

- [ ] `POST /usage` 返回 404/405(路由不存在)
- [ ] GET /usage 与公开镜像的查询投影行为不变(Dashboard Timeline 正常)
- [ ] usage 上传 DTO 与 usage 校验策略在解决方案内无引用残留,连带测试删除
- [ ] 服务端测试全部通过

## Blocked by

- [03](./03-monitor-segments-through-hub.md)
