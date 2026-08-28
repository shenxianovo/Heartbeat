# 03 — 裁决兼容债务支持窗口与移除顺序

Status: ready-for-human

Owner: Cross-context / Compatibility Retirement

Priority: P2 — 真实兼容对象仍在服务，但应在阻碍原生 Subject/Fact 与 Package/Instance 模型前退出。

- [ ] 逐条确认 `docs/architecture/compatibility-debt.md` 的兼容对象是否仍真实存在。
- [ ] 为 Agent、Browser Extension、Headless 本地状态和服务端数据分别确定最低支持版本或最长离线窗口。
- [ ] 区分“待删除 adapter”与“有意长期保留的稳定投影边界”，后者从债务改写为正式架构说明。
- [ ] 每个待删除项有 owner、删除触发条件、fixture 与回滚/恢复验证，不按日期直接删除本地数据迁移代码。
- [ ] 优先处理阻碍新模型演进或让双写产生不一致的债务；纯 DTO 展示别名可后置。
