# ADR-011: GitHub Releases as Update Source (No CDN Mirror)

## Status: Accepted

## Date: 2026-05-19

## Context

Velopack 客户端需配置更新源 URL。候选：

1. **GitHub Releases 直连** — 简单,通过 Velopack 的 `GithubSource` 指向仓库 `https://github.com/shenxianovo/Heartbeat`,由 Velopack 自行解析 latest release 的资产。
2. **国内 CDN 镜像（阿里云 OSS / 腾讯 COS）** — 国内用户访问 GitHub 不稳定，CDN 可提升下载成功率。
3. **自有服务器中转** — 灵活但增加运维负担和带宽成本。

## Decision

当前阶段桌面端 **直连 GitHub Releases**（`RepoUrl = "https://github.com/shenxianovo/Heartbeat"`），暂不引入 CDN 镜像。Windows 与 macOS 共用这一发布源；macOS 走站外直接分发而非 Mac App Store，首个发布目标只包含 Apple Silicon `arm64`，按用户安装到 `~/Applications` 以避免更新时要求管理员提权。macOS 发布物的信任策略后来由 [ADR-039](./039-unsigned-macos-velopack-release.md) 修订为无 Developer ID 签名与公证、保留 Velopack 自动更新。

原因：
- 个人项目，用户量小，GitHub 可用性可接受
- 减少基础设施依赖和成本
- 已有自有服务器，后续如需切换只需发一版新客户端改 URL

## Consequences

- ✅ 零额外成本，零运维
- ✅ 发布流程简单：tag → build → upload to Release
- ✅ macOS 不受 App Store 沙箱约束，可提供全局活动监控能力
- ⚠️ 国内用户可能下载失败/超时
- ⚠️ 切换更新源需发布一个过渡版本（旧版本仍指向 GitHub）
- ⚠️ macOS 未签名发布物的 Gatekeeper 放行、更新重启和 TCC 权限连续性必须在真机验证；详见 ADR-039
