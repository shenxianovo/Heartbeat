# ADR-011: GitHub Releases as Update Source (No CDN Mirror)

## Status: Accepted

## Date: 2026-05-19

## Context

Velopack 客户端需配置更新源 URL。候选：

1. **GitHub Releases 直连** — 简单，URL 硬编码为 `https://github.com/shenxianovo/Heartbeat/releases/latest/download`。
2. **国内 CDN 镜像（阿里云 OSS / 腾讯 COS）** — 国内用户访问 GitHub 不稳定，CDN 可提升下载成功率。
3. **自有服务器中转** — 灵活但增加运维负担和带宽成本。

## Decision

当前阶段 **硬编码 GitHub Releases URL**，暂不引入 CDN 镜像。

原因：
- 个人项目，用户量小，GitHub 可用性可接受
- 减少基础设施依赖和成本
- 已有自有服务器，后续如需切换只需发一版新客户端改 URL

## Consequences

- ✅ 零额外成本，零运维
- ✅ 发布流程简单：tag → build → upload to Release
- ⚠️ 国内用户可能下载失败/超时
- ⚠️ 切换更新源需发布一个过渡版本（旧版本仍指向 GitHub）
