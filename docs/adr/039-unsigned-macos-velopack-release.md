# ADR-039: 未签名 macOS 发布保留 Velopack 自动更新

## Status: Accepted

## Date: 2026-08-15

## Context

Heartbeat 需要直接分发具备全局活动观测能力的 macOS 应用，并希望 Windows 与 macOS 共用 GitHub Releases 和 Velopack 更新生命周期。Developer ID Application、Developer ID Installer 与 Apple 公证需要付费 Apple Developer Program 资格；当前维护者接受首次运行由用户在“隐私与安全性”中手动放行，不接受因此退化为人工替换 ZIP。

Velopack 可以在没有 Apple 签名和公证参数时生成 Setup、Portable、完整包、增量包与更新 feed，也能在应用内下载、校验、替换并重新启动应用。Velopack 不负责 Gatekeeper、quarantine 或 TCC 权限恢复。无 Developer ID 身份时，稳定 bundle identifier 和安装路径仍然有价值，但不足以保证 Accessibility 与 Input Monitoring 在更新后必然延续。

## Decision

macOS 首发继续以 `osx-arm64`、bundle identifier `com.shenxianovo.heartbeat`、稳定频道 `osx-arm64-stable` 和每用户 `~/Applications` 安装发布。GitHub Actions 在 macOS runner 上构建并用 Velopack 生成发布物，但不导入 Apple 证书、不签署 Developer ID、不提交公证，也不需要任何 Apple Developer secrets 或 variables。

无签名不改变自动更新产品语义。客户端继续通过 GitHub Releases 检查版本、下载 Velopack 完整包或增量包、校验包、调度 UpdateMac、替换当前应用并重新启动。发布流程保留前一版本作为 delta 基线；首次或基线不可用时退化为完整包，不退化为人工替换应用。

安装说明必须明确首次下载可能被 Gatekeeper 阻止，并指引用户在 System Settings > Privacy & Security 中选择 Open Anyway。产品和文档不得声称未签名应用能无警告启动，也不得声称 Velopack 能处理 Gatekeeper、quarantine 或 TCC。

稳定 bundle identifier、可执行文件名与安装位置跨普通更新保持不变。每个候选 Release 在推广前必须于真实 Apple Silicon 设备演练 `vA -> vB`：首次安装与放行、Accessibility/Input Monitoring 授权、更新发现/下载/应用/重启、继续采集，以及权限是否保持或需要重新授权。观察结果属于发布证据；在获得证据前，不对权限连续性作保证。

未来获得 Apple Developer 资格时，可以在不改变 bundle identifier、Velopack channel 或更新源的前提下恢复 Developer ID 签名与公证。首次从无 Developer ID 身份迁移到稳定签名身份仍需单独验证 Gatekeeper 与 TCC，不假设它是透明更新。

## Consequences

- ✅ 不需要付费 Apple Developer 资格或 CI 中的 Apple 凭证。
- ✅ 保留 Velopack 的完整/增量下载、原位替换和自动重启生命周期。
- ✅ Windows 与 macOS 继续由同一个 tag 和 GitHub Release 发布。
- ✅ 未来可以在同一发布架构上增加签名与公证。
- ⚠️ 首次下载可能被 Gatekeeper 阻止，用户必须手动放行。
- ⚠️ Velopack 无法替应用解决 quarantine、Gatekeeper 或 TCC 授权问题。
- ⚠️ Accessibility 与 Input Monitoring 是否跨更新保持必须用真实版本对验证，可能需要用户重新授权。
- ⚠️ 无 Developer ID 的信任和安装体验不适合面向不愿执行手动放行的普通用户。

## References

- [ADR-009](./009-velopack-auto-update.md) — Velopack 更新生命周期
- [ADR-011](./011-github-releases-update-source.md) — GitHub Releases 更新源
- [`release-desktop.yml`](../../.github/workflows/release-desktop.yml) — Windows/macOS 发布工作流
