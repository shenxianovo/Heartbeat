# 01: 确定性地基 —— Handle 派生 + Strand/Mute 数据模型 + 提交端点

Status: done

## Parent

[ADR-028](../../../docs/adr/028-strand-knowledge-layer.md)

## What to build

搭知识层的确定性底座:把手派生、Strand/Mute 落库、以及 Dashboard 写回的提交端点。**本片不含 LLM、不含提问器、不含 UI**——那些坐在这层之上（issue 02/03）。

- **Handle 派生（纯函数，ADR-028 §3）**：从一条 segment 算出粗把手 `(Source, Token)`——`browser→domain`（取 `Attributes.domain`，缺失则从 url 派生）、`system→AppName`、`vscode→仓库根`（采集器未落地，留占位/未实现分支）。是 IdentityKey 的粗化/派生，不是 IdentityKey 本身。配单元测试。
- **Strand 实体**：`Id`（UUIDv7）、`OwnerId`（= JWT `sub`，string）、`Name`、`Gloss`、成员 Handle 集（按值存 `(Source, Token)`）、`UpdatedAt`（staleness 后续用，本片即写）。per-Owner 隔离。
- **Mute**：对一个锚点 Handle 的负向裁决，per-Owner。Strand 绑定与 Mute **同住知识库**——一张表加判别位还是两张表由实现定（ADR-028 §8），但都按 `OwnerId` 隔离、Handle 按值存。
- **EF Core 迁移**：新增表 + 迁移脚本（现有自动迁移策略见 ADR-013）。
- **提交端点（`Dashboard → Analytics` 写路径，ADR-028 §5/CONTEXT-MAP）**：
  - 绑定：建/改 Strand（name/gloss/成员），按 Id upsert，**幂等**（重复提交收敛，不产重复行）。
  - Mute：静音一个 `(Source, Token)` 锚点把手，**幂等**。
  - 两端点走 JWT，`OwnerId` 取 `sub`；匿名/跨 owner 拒绝。

## Acceptance criteria

- [x] Handle 派生纯函数单测:browser url→domain、system→AppName、vscode 占位分支;IdentityKey→Handle 粗化关系有覆盖
- [x] Strand/Mute 落库,按 `OwnerId` 隔离(跨 owner 读不到、改不动,Service 层显式过滤)
- [x] POST 绑定:建/改 Strand(name/gloss/成员)入库,重复提交幂等收敛
- [x] POST Mute:静音 `(Source, Token)` 入库,幂等
- [x] 两端点未带 JWT / 跨 owner 时拒绝(401/403)
- [x] EF 迁移可正向应用,现有 segments/recap 表无回归

## Comments

- 2026-07-19 实现落地:实体 `Strand`/`StrandHandle`/`MutedHandle` + 迁移 `AddStrandKnowledgeLayer`(纯增表);`KnowledgeService`(绑定按 Id 或 (OwnerId,Name) 收敛、成员整组替换、Mute 幂等)+ `KnowledgeController`(`POST api/v1/knowledge/strands|mutes`);`HandleDerivation` 纯函数;`ActivitySources.Browser` 常量入 Core。测试 15 个新增,服务端套件 85/85 绿(Testcontainers 真库跑真迁移)。前端 OpenAPI client 重生成留到 issue 03(本片无前端消费方)。

## Blocked by

（无）
