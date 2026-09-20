Status: needs-info

# 桌面回放中的 Auth 与浏览器阶段偶发超时

2026-09-20，真实客户端曾显示 `Auth request timed out`，重试后成功完成认证和钥匙串保存。先前独立 .NET 请求也曾在 TLS 握手和读取响应时超时后恢复；证据 `.artifacts/auth-diagnosis/native-auth-success.json`。未证明是 key、钥匙串或 IPv6 导致，不增加永久网络回退或掩盖性重试。

同日 `scenario desktop-replay` 首次到达真实浏览器后，在粗粒度 `record-query` 阶段超时；已落库且客户端队列为零。证据 `.artifacts/verification/20260920T071125Z-scenario-desktop-replay-a1f1b11deb5242459eff623a0ab74495/manifest.json`、`replay.json`。当次未保存具体步骤或 API 状态，不能确认是哪一个请求或页面操作失败，也不能认定与 Auth 网络超时同源。

后续只补充验证脚本诊断，将查询分成 track-query、range-input、record-response、timeline-selection，失败仅保存最近 API 路径与状态码，不保存 token、认证 URL、响应体或原生页面文本。真实主线最终通过：`.artifacts/verification/20260920T072545Z-scenario-desktop-replay-af5d844ec8ee409dab93206736bfbaae/manifest.json`。没有修改认证或前端查询逻辑，因此该次通过不能视为根因修复。

## 再现入口

`./scripts/heartbeat-dev scenario desktop-replay --keep-environment-on-failure`

完成临时客户端配置、前台采集、暂停排空和退出，在临时浏览器完成真实 OIDC 登录。若再次超时，根据 `replay.json` 的具体阶段及 API 状态区分请求失败、时间范围操作、响应匹配或详情选择；保留环境仅检查受控 Record 元数据。不要为排查导出浏览器凭据或其他原生载荷。

## Comments

- 2026-09-20：保留失败和随后成功两份证据；当前无法稳定复现，尚无产品根因结论。
