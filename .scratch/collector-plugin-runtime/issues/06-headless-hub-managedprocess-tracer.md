# 06 — 无头 Hub 运行 ManagedProcess 参考 Collector

**What to build:** 提供可独立运行的无头 Hub，并让它从本地 Package/Instance 配置启动一个确定性的 ManagedProcess 参考 Collector，完成协议、接收 Account Segment、上传和优雅停止，以验证服务器常驻宿主路径。

**Blocked by:** 01 — 本地参考 Package 跑通 Collector Protocol.

**Status:** done

- [x] 无头 Hub composition 不依赖桌面 UI、前台窗口 API 或平台发布供应商，可以使用现有认证、缓存和上传能力独立启动。
- [x] Runtime 按 Manifest 选择唯一兼容 ManagedProcess Artifact，启动进程但只有完成 initialize、Stream 和 Ready 后才视为 Activation 可用。
- [x] 参考子进程面向 Account Subject 发布一个 Segment，经 Hub 进入现有上传责任边界；运行服务器的 Machine 不会被写成 Subject。
- [x] 进程退出、协议损坏和启动超时形成结构化 Runtime State；断连结束 Activation 并释放 writer。
- [x] Hub 停止时发送 drain，等待有界宽限期后再终止仍未退出的子进程，并保留真实 pending Fact/Gap 诊断。
- [x] ManagedProcess 与 InProcess 使用同一组逻辑 transcript contract tests，差异只存在于 Binding 和进程控制。
