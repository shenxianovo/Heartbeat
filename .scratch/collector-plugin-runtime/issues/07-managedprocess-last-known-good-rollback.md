# 07 — ManagedProcess 更新失败恢复 Last-Known-Good

**What to build:** 让单个 ManagedProcess Collector Instance 可以从当前 Package 切换到候选版本；候选未 Ready 或很快失败时，Runtime 结束候选并用上一已知良好 Package 创建新 Activation，其他 Collector 不受影响。

**Blocked by:** 06 — 无头 Hub 运行 ManagedProcess 参考 Collector.

**Status:** done

- [x] 更新开始前保留当前精确 Package、Artifact、配置版本和 Last-Known-Good 记录；候选不能接受当前配置时在停止旧 Activation 前被阻断。
- [x] Runtime 先停止旧 Activation 并释放 writer，再启动候选；本期不要求双活、零停机或 lease 抢占协议。
- [x] 只有候选完成协议、取得全部必需 Stream 并到达 Ready 后，安装解析状态才提交为当前版本。
- [x] 候选失败时结束其 Activation，并以旧 Package 创建新的 Activation；CollectorInstanceId 与兼容 StreamId 保持稳定，ActivationId 必须变化。
- [x] 回滚失败、旧 Package 缺失和候选/旧版本都无法启动时形成可操作诊断，不改写 Desired State。
- [x] 成功更新、握手失败、Ready 前崩溃和回滚成功均由确定性 fixture 覆盖。
