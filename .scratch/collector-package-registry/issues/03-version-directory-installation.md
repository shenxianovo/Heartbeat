# 03 — 实现版本目录 Collector Installation

Status: ready-for-agent

Owner: Collection / Package Delivery

Priority: P1 — Runtime 只能运行完整下载并验证过的 Package，不能把半成品叫作 Installation。

## What to build

实现一个窄的 Package installer：读取 issue 01 index、下载精确 artifact、校验 length/SHA-256 与 Package
内部内容，安全解压到独立版本目录，最后写完成标记。模块返回精确 Installation 或结构化失败，不负责
Activation，也不实现全局 solver、journal、离线目录或 cache GC。

## Acceptance

- [ ] 精确 PackageId/version/hash 映射到独立目录；目录只有在完成标记存在且内容仍匹配时才是 Installation。
- [ ] 下载校验 length/hash；Package loader 校验 manifest/artifacts/schema/declarations；解压拒绝绝对路径、
  `..` 与目标目录外写入。
- [ ] 下载、解压或校验失败不触碰当前/LKG；无完成标记的目录可在下次尝试直接清理或覆盖。
- [ ] 重复安装同一精确候选幂等；同 Version 异 hash 使用不同目录且不得冒充已有 Installation。
- [ ] Registry 不可达、断流、磁盘不足、取消、错 hash 与损坏 Package 返回稳定错误。
- [ ] 单元、故障注入和进程重启后忽略未完成目录的测试通过。

## Dependencies

依赖 issue 01 的 index contract 与 fixtures。
