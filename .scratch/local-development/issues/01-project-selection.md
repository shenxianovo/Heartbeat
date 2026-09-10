# 01 — 统一启动项目参数与独立就绪检查

Status: done

## 验收

- [x] sh / pwsh 共用 `--backend --frontend --hub --desktop --collector browser --stack` 参数语义。
- [x] 无项目选择默认三件套；显式选择替换默认；重复项目去重，只补齐 db / Collector 宿主。
- [x] Browser 应用通过 `--browser-app chrome|edge` 选择，保留监听与状态保持更新。
- [x] 只构建启动所选 Compose 服务，不停止/重建未选实例；Desktop/Collector 单独启动不调用 Docker。
- [x] 后端、前端、Hub 独立地址和检查；Desktop 直连本地后端，保持开发 Profile 隔离。
- [x] 退役旧 only / Browser 参数，修正文档；退出码、非法参数和启动失败可判定。
- [x] 自动回归、真实 sh / pwsh 参数转发和隔离 Compose 验证完成。

## Comments

2026-09-10：参数与启动逻辑归入内部 Node 模块，sh / pwsh 保留为一致的薄入口，所有模式需 Node.js 24+。
先编写选择、依赖闭包、单服务检查与失败传播测试确认失败，再实现共同入口。生产 Compose 不改动。
现有 Browser 的 Windows/Edge 和 Analytics 到达 gate 继续归原 issue，不由本条关闭。


2026-09-10 closeout：

- `node --test scripts/start-local.test.mjs scripts/browser-development.test.mjs`：10 项通过，覆盖默认/显式选择、
  依赖补齐、独立探测、失败退出码、超时、拒绝非 loopback、Windows 无强杀分支及真实父子进程 IPC 清理。
- Bash 语法、Compose 配置和 `git diff --check` 通过。实际解析 compose.local.yml 验证三个端口均只绑定 loopback。
- 真实 Bash + 便携 PowerShell 7.5.4（macOS）执行隔离 Compose 项目：单独前端/后端/Hub、默认三件套、
  含空格的配置路径、旧参数退出码 2 均通过。自定义前端声明 Hub 依赖也不带起 Hub，未选容器 ID 不变。
  测试使用独立名称和 nginx fixtures，结束后删除全部测试容器/网络；未操作用户正在运行的开发栈。
  证据/可重复入口：`.local/start-local-verification/compose-smoke-log.json`、`compose-smoke.py`。
- 实际 Browser 编排构建并启动独立 macOS Desktop，IPC 请求停止后退出 0，`.owner` 释放。
  证据：`.local/start-local-verification/browser-ipc-smoke-report.json`、`browser-ipc-smoke.mjs`。
- Standards：Windows 信号强杀问题已修复，复查无剩余硬问题；Spec：自定义 Compose 隐式依赖和退出路径问题
  已修复，复查无剩余实现问题。旧重复等待脚本已由共用就绪检查替代并删除；所有当前操作文档已同步。
- 验证边界：上述 PowerShell 执行发生在 macOS，不替代 Windows 原生 Console Ctrl+C 或 Windows/Edge 实机验收。
  这些平台验收继续由 [Browser issue 01](../../browser-development/issues/01-profile-binding-and-update.md) 承接。
