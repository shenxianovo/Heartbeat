# ADR-010: Per-User Installation to %LocalAppData%

## Status: Accepted

## Date: 2026-05-19

## Context

桌面客户端需要选择安装位置。候选：

1. **Program Files（per-machine）** — 标准位置，但写入需 UAC 提权。每次自动更新都弹 UAC，受限账户无法更新。
2. **%LocalAppData%（per-user）** — 当前用户有完全写权限，无需 UAC。Chrome、VSCode、Discord、Slack 均采用此方案。
3. **自定义路径** — Velopack 不支持，更新器需可预测路径定位安装目录。

## Decision

安装到 **`%LocalAppData%\Heartbeat`**（per-user）。

## Consequences

- ✅ 安装和更新均无 UAC 弹窗，静默完成
- ✅ 受限账户（公司电脑非管理员）也能正常更新
- ⚠️ 多用户共享同一台电脑时需各自安装一份
- ⚠️ 用户无法自选安装路径（Velopack 限制）
