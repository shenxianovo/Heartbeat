# VRChat 真实账号验收

Status: ready-for-human

登录流程实现、自动验证及旧 Desktop 进程混用问题已完成，决策见 [ADR-0019](../../../docs/adr/ADR-0019-single-owner-hub-management.md)，证据见 [验收记录](../../../docs/validation/hub-management.md)。自动测试的第三方 API 是替身，以下线上账号行为尚未验收。

使用自己的账号在 Web 选择服务器 Hub 并登录，完成实际要求的验证码；进入 VRChat 世界后确认位置 Record 可回放。重启服务器应恢复采集；会话失效后从原账号行重新登录应自动恢复。不将凭据写入文档。
