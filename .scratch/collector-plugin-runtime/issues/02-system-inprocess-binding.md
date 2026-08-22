# 02 — system Collector 切换到 InProcess Binding

**What to build:** 让 Windows 与 macOS 的内置 system Collector 以 BuiltIn Package 和稳定 Instance 身份运行，并通过与参考 Collector 相同的 Collector Protocol 交付前台活动 Segment，不再把 ISegmentSink 当成 system 独有协议。

**Blocked by:** 01 — 本地参考 Package 跑通 Collector Protocol.

**Status:** ready-for-agent

- [ ] system Collector 通过 InProcess Binding 完成 Activation、打开 foreground Segment Stream 并发布带稳定 FactId/Revision 的完整快照。
- [ ] 相同桌面观察序列产生的 Source、AppIdentity、标题、起止时间和快照增长行为与迁移前一致。
- [ ] Windows 与 macOS composition 都使用同一协议适配器，并通过共享 transcript 与现有平台场景测试。
- [ ] Current Activity、per-Source last-seen 和 Hub→Analytics presence 明确保留为 Hub 读模型，不伪装成 Fact，也不因本次迁移退化。
- [ ] 迁移期间旧 Segment 上传格式仍可由 adapter 使用；system Collector 本身不再直接依赖旧摄入契约。
