# 05 — InputEvent 作为 Event Fact 接入

**What to build:** 让 system Collector 的真实输入记录作为 Event Fact 通过同一 Activation 和 Collector Protocol 进入 Hub，再适配到现有 InputEvent 上传与 Analytics 路径，从而用生产数据验证第二种 Fact 家族。

**Blocked by:** 02 — system Collector 切换到 InProcess Binding.

**Status:** ready-for-agent

- [ ] system Package 声明 Input Event Output，启用输入记录时在同一 Activation 中打开 Event Stream。
- [ ] 每个输入事件使用稳定 FactId、Revision 1、occurredAt 和现有 CodeSet/code；默认 Event 的更高 present Revision 被拒绝。
- [ ] ACK 丢失和重传不会在 Hub 或 Analytics 产生重复 InputEvent；永久 schema 错误进入可诊断的 dead letter。
- [ ] 现有 Input Event Recording 用户意图、权限状态和 Interaction Signal 的本地瞬时语义保持不变。
- [ ] 现有服务端查询与 UI 行为无需先迁移物理表即可继续工作，协议适配层有覆盖测试。
