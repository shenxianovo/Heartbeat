# 04 — Collector page 管理旁加载 browser Package

**What to build:** 让没有浏览器应用商店发布能力的用户，也能由 Desktop Hub 持有和管理本地 browser Collector Package：导入版本化制品、查看旁加载位置和实际 Activation 状态，并管理稳定 Instance 的启用意图。

**Blocked by:** 03 — browser Collector 切换到 ExternalHost Binding.

**Status:** ready-for-agent

- [ ] 用户可以从本地 Package 导入 browser 制品；Runtime 校验 Manifest、平台选择和内容哈希后记录精确安装版本，不把“目录存在”冒充为浏览器已加载。
- [ ] Collector page 展示 Package 版本、Desired Enabled、ExternalHost 的 Waiting/Ready/Degraded 状态和可操作的旁加载目录说明。
- [ ] 启用或停用修改稳定 Collector Instance 的 Desired State；浏览器未运行时保持 Waiting，而不是删除 Instance 或安装事实。
- [ ] 本地 Package 更新以新版本目录暂存，保留上一已知良好版本；需要用户在浏览器中 reload 的动作被诚实呈现。
- [ ] system Collector 仍不可停用；现有 Source Registry 在迁移期只作为 legacy adapter，不再被 UI 当成 Package 安装事实。
