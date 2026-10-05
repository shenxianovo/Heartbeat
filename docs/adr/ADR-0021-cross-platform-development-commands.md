# ADR-0021：跨平台开发入口和配置准备归 DevCLI

## 状态：已接受

## 日期：2026-09-21

开发者在 macOS 和 Windows 使用相同的 DevCLI 子命令；平台差异封装在实现中。统一从仓库根目录执行 `dotnet run --project tools/Heartbeat.Dev -- <子命令>`；删除 `scripts/`，不再维护各平台的启动包装器。原 `setup.sh` 迁为 `env setup`，删除原脚本及其未使用的通用 wizard 功能；此决策扩展 [ADR-0018](ADR-0018-developer-cli-packaging.md) 的开发工具职责。

`env setup` 负责交互输入、私密暂存和成功后的原子保存，复用现有 Hub `--check-auth` 校验真实 Auth 身份；Owner 的权威仍是 Auth 经 Hub 验证的结果，不由 CLI 推导。仓库 `.env.local` 保存本地 Hub 配置；现有 Owner 绑定不得因换 key 静默改变，失败或取消不覆盖原文件。此流程与桌面应用自身的连接设置及系统凭据库存储分别服务各自宿主，不互相复制凭据。

`env up desktop` 在宿主 OS 上打包并独立打开 AppKit 或 WinUI 应用；CLI 退出不停止桌面客户端。Windows 继续使用未打包的 self-contained 目录和当前会话的 Win32/Raw Input 能力，不存在 macOS 的 TCC 辅助功能/输入监控授权流程。因此两端共用 `signing setup/status` 命令，但 Windows 明确报告无需开发签名，不创建或信任自签名证书，不把固定证书误当作解除 Windows 安全限制的办法。macOS 仍遵循 [ADR-0020](ADR-0020-stable-macos-development-signing.md)。

本决策不增加发行签名、公证、MSIX、SmartScreen 信任或安全桌面访问能力。Windows 构建、ACL、启动与原生观测必须在 Windows 实机验收，Mac 上的路由与组合测试只证明共享编排。
