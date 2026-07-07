# 01: 冒充守卫上移 loopback 层 + ingest 请求处理可测化

Status: done

## Parent

[ADR-020](../../../docs/adr/020-system-collector-through-hub.md)

## What to build

把"拒收 `source='system'`"的冒充守卫从 hub 的缓冲模块(`Accept`)上移到 loopback 传输层——它防的是"谁在调"(本机进程冒充),不是数据合法性。同时把 ingest worker 的请求处理(路由、JSON 解析、状态码映射、accepted 计数响应)提取为可脱离 `HttpListenerContext` 构造的可测单元:守卫不能搬进零覆盖区。

完成后 `Accept` 变 source 无关,为 issue 03 里 monitor 进程内直调铺路;插件作者的 HTTP 契约(404/400/500、错误体、`{"accepted":n}`)第一次获得测试。

## Acceptance criteria

- [ ] 缓冲模块的 `Accept` 不再检查 source,system 段可经进程内调用进入缓冲
- [ ] loopback 路径 POST `source='system'` 仍返回 400(守卫在传输层生效)
- [ ] 请求处理单元测试覆盖:错误路径/方法 404、非法 JSON 400、空批 400、system 冒充 400、正常批 200 + `{"accepted":n}`
- [ ] worker 只剩 HttpListener 生命周期与上下文搬运,无协议逻辑
- [ ] 现有 `SegmentIngestServiceTests` 相应迁移,全部测试通过

## Blocked by

None - can start immediately
