# 01: 层级与时间化 Strand

Status: ready-for-agent

## Parent

[PRD](../PRD.md) · [ADR-031](../../../docs/adr/031-hierarchical-strand-episode-teaching-loop.md)

## What to build

把现有扁平 Strand 写模型迁移为严格单父级、带近似本地日期范围的语境树，并把现有知识实体的数据库身份统一到应用层生成的 UUIDv7。

### Persistence and migration

- 为 Strand 增加可空 `ParentStrandId`、持久化 `NormalizedName`、可空 `StartedOn` / `EndedOn`；日期按用户本地叙事日解释，不承担工时精度。
- 现有 Strand 保留 UUID，全部迁为日期未知的顶层节点；保留 Name、Gloss、Matcher、Mute 和审计时间。
- 将现有自增 `StrandMatcher.Id`、`MutedMatcher.Id` 迁为 UUIDv7，更新所有引用和 DTO。以后由应用层生成 ID，不依赖数据库自增。
- Matcher/Mute 谓词的业务等价性仍由 canonical path/value 决定；UUID 只是持久化身份，不能改变去重和匹配语义。
- 数据迁移必须可在含现有 Strand/Matcher/Mute 数据的数据库上执行，不删除或重新归属用户知识。

### Domain service and API

- 创建、编辑、结束和移动 Strand 全部以 UUIDv7 ID 定位；禁止用 Name 隐式绑定已有 Strand。
- 支持任意实用深度的树读取，返回稳定的 parent ID，并让调用方能展示根到节点的 path。
- 在确定性服务层统一验证：
  - 不能把节点放到自身或后代下，任何写入后树均无环；
  - `StartedOn <= EndedOn`；
  - 同 Owner、同 Parent、同 `NormalizedName` 的有效日期范围不得重叠，未知端点按无界处理；
  - 子节点的已知日期端点不得越出父节点的已知范围；
  - 父节点可以没有 Matcher；
  - 不能跨 Owner 引用父级或 Matcher/Mute。
- `MoveStrand` 明确是历史纠错，会改变该节点及后代的历史解释；现实归属变化不提供“定时换父级”，由调用方结束旧 Strand 后创建新 Strand。
- 结束仍有活跃子节点的父级时不得静默级联。API 返回可解释冲突及活跃子节点清单，供后续 UI 选择先结束子级、建立后继或整理层级。
- 更新和移动暴露可供教学协议使用的并发版本或等价条件写能力，避免陈旧提案覆盖新编辑。

### Tests

- 为迁移、树约束、日期范围、同名不重叠、跨 Owner 隔离及 UUIDv7 身份增加自动化测试。
- 覆盖移动到后代、开放日期端点、父子日期部分已知、相邻但不重叠的同名时期等边界。

## Acceptance criteria

- [ ] 现有 Strand 数据升级后均为顶层、日期未知且原 UUID/名称/Gloss/Matcher/Mute 完整保留
- [ ] StrandMatcher 与 MutedMatcher 不再使用自增 ID，新建记录由应用层生成 UUIDv7
- [ ] 已有 Strand 的所有写操作按 ID 定位；同名不再成为隐式更新或绑定依据
- [ ] 服务层可阻止环、跨 Owner 父级、非法日期和同父同名的重叠有效范围
- [ ] 子节点已知日期范围不能超出父节点已知范围，未知端点按无界规则一致处理
- [ ] 父级可零 Matcher；读取树时可获得稳定的祖先路径
- [ ] 结束含活跃子节点的父级返回显式冲突，不静默结束或移动子节点
- [ ] 更新接口具备并发冲突检测，供后续 KnowledgeChangeSet 原子提交复用
- [ ] 数据迁移与领域/API 测试覆盖主要成功和失败路径

## Blocked by

None - can start immediately

## Comments
