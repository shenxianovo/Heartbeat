# Collection

采集 Windows 前台窗口使用时长，上传至服务端。作为常驻托盘应用运行。

## Language

**Agent**:
后台采集引擎，负责监听窗口切换、生成使用记录、缓存与上传。
_Avoid_: Service, Worker（这些是 Agent 内部的实现层）

**Setup**:
Velopack 生成的安装器（Setup.exe），用户首次安装时下载运行。
_Avoid_: Installer

**Release**:
一次发布产物的集合，包含 Setup、完整包（.nupkg）和元数据文件（RELEASES），托管在 GitHub Releases。
_Avoid_: Build, Package

**Update**:
应用检测到新版本后，下载增量/完整包并在用户确认重启后应用的过程。
_Avoid_: Upgrade, Patch

## Relationships

- 一个 **Release** 包含一个 **Setup** 和一个完整包
- **Agent** 在应用生命周期内持续运行，**Update** 需重启应用才能生效

## Flagged ambiguities

- "安装" 既指首次 Setup 安装，也指更新后的应用替换 — 统一用 **Setup**（首次）和 **Update**（后续）区分。
