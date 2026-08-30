# Collector 独立交付实现路线图

本文把 [ADR-045](../adr/045-independent-web-delivery-for-collector-packages.md) 的实现拆成两个已有
tracker 的 feature，并固定它们之间与各自内部的依赖顺序：

- [Collector 数据可靠性收口](../../.scratch/collector-reliability-closeout/PRD.md)
- [Collector Package Registry](../../.scratch/collector-package-registry/PRD.md)

箭头表示“右侧开始或完成前必须满足左侧条件”，不是要求所有工作串行。没有依赖箭头的节点可以并行。
Analytics + Dashboard 原子发布、Headless production configuration 是扫描发现的独立应用/运维平面
风险，不塞进这两个 Collection feature；它们必须在对应平面实际发布前单独 closeout。

## Feature 之间的实现顺序

Registry contract、release tooling 和 Runtime delivery module 可以在可靠性修复期间开发；任何会改变
真实用户安装的 migration/rollout 必须等待 P1 reliability gate。System 始终走 Desktop BuiltIn
Delivery，不等待 Web Registry。

```mermaid
flowchart LR
    D["设计与账本基线<br/>ADR-045 + PRDs"]
    R["Feature A<br/>Collector 数据可靠性收口"]
    C["Feature B1<br/>Registry contract + release tooling"]
    M["Feature B2<br/>Runtime delivery + owner approval"]
    X["Feature B3<br/>ManagedProcess / ExternalHost 接入"]
    G{"P1 reliability gate"}
    P["Feature B4<br/>真实安装迁移与 Registry 上线"]
    S["System Collector<br/>继续随 Desktop BuiltIn 发布"]

    D --> R
    D --> C
    C --> M --> X
    R --> G
    G --> P
    X --> P
    D --> S

    classDef gate fill:#fff2cc,stroke:#b8860b,color:#3d3200;
    classDef rollout fill:#d9ead3,stroke:#38761d,color:#183b0b;
    class G gate;
    class P rollout;
```

应用与运维平面的发布门禁与上图并行，不阻塞纯 contract/local implementation，但阻塞各自的生产发布：

```mermaid
flowchart LR
    A0["盘点当前应用部署拓扑"] --> A1["Frontend + Analytics 单一 release identity"]
    A1 --> A2["禁止 mixed-version traffic"]
    A2 --> A3["整单元 deploy / rollback smoke"]
    A3 --> AP["应用平面可发布"]

    H0["盘点真实 Headless 配置与 Secret"] --> H1["startup fail-fast validation"]
    H1 --> H2["production compose / deployment wiring"]
    H2 --> H3["真实账号 smoke + recovery runbook"]
    H3 --> HP["Headless 平面可发布"]
```

这两条并行线目前是扫描结论，不冒充已建立的 implementation feature；开始实现时应分别建立 tracker，
并以真实 deployment state 为输入。

## Feature A：Collector 数据可靠性收口

数据真实性分成两条可并行 lane。Segment lane 先修服务端 truthful outcome，再用相同 strict contract
验证长时段旋转；Protocol lane 可以同时处理 InputEvent capacity 和有界 drain。四项全部完成后执行
跨进程 restart/replay smoke，才关闭 gate。

```mermaid
flowchart TD
    B["固定当前失败复现与真实 cache fixture"]

    S1["A1 · strict Segment ingest<br/>拒绝整批并返回 400/422"]
    S2["A2 · 连续 Segment rotation<br/>System + VRChat 在 24h 前切段"]
    S3["A3 · 长会话 HTTP E2E<br/>合法 chunks + union duration"]

    I1["A4 · durable InputEvent capacity<br/>backpressure 或原子 Stream Gap"]
    D1["A5 · bounded InProcess drain<br/>deadline 覆盖 Stop + flush"]
    P1["A6 · Protocol/restart smoke<br/>facts + gaps + truthful remainder"]

    G{"Reliability PRD done"}

    B --> S1 --> S2 --> S3 --> G
    B --> I1 --> P1 --> G
    B --> D1 --> P1

    classDef gate fill:#fff2cc,stroke:#b8860b,color:#3d3200;
    class G gate;
```

对应 tracker：

1. [strict Segment ingest](../../.scratch/repository-understanding/issues/05-strict-segment-ingest-outcomes.md)
2. [连续 Segment rotation](../../.scratch/collector-reliability-closeout/issues/01-rotate-continuous-segments.md)
3. [durable InputEvent capacity](../../.scratch/collector-reliability-closeout/issues/02-durable-input-capacity.md)
4. [bounded InProcess drain](../../.scratch/collector-reliability-closeout/issues/03-bounded-inprocess-drain.md)

其中 A1 → A2 是推荐的 tracer 顺序：先让下游拒绝结果可信，再证明上游永远生成合法 chunk。A4、A5
不依赖 Segment 代码，可以与 A1/A2 并行。

## Feature B：Collector Package Registry

Registry schema/golden fixtures 是发布方和 Runtime 的共同地基。Release pipeline 可以先搭矩阵框架，
但在签名/serialization contract 固定前不能发布 stable。Delivery module 完成后才向管理面暴露 offer；
ManagedProcess 与 Browser adapter 随后并行，最终共同进入人工 rollout。

```mermaid
flowchart TD
    C1["B1 · signed static Registry contract<br/>schema + canonical bytes + golden fixtures"]
    C2["B2a · release pipeline scaffold<br/>tag + platform matrix + dry-run"]
    C3["B2b · canonical assembly/publish<br/>sign + blob → release → channel"]
    D1["B3 · package delivery deep module<br/>solve + verify + atomic install + offline"]
    U1["B4 · per-Instance Update Offer<br/>Current + CheckNow + Approve"]
    M1["B5 · ManagedProcess adapter<br/>Ready + stability + LKG"]
    E1["B6 · Browser ExternalHost adapter<br/>stage + reload + exact hash Ready"]
    R{"Feature A reliability gate"}
    P1["B7 · production key + static hosting"]
    P2["B8 · 真实安装 migration"]
    P3["B9 · Browser/VRChat smoke + rollback"]

    C1 --> C3
    C2 --> C3
    C1 --> D1 --> U1
    U1 --> M1
    U1 --> E1
    C3 --> P1
    M1 --> P2
    E1 --> P2
    R --> P2
    P1 --> P2 --> P3

    classDef gate fill:#fff2cc,stroke:#b8860b,color:#3d3200;
    class R gate;
```

issue 映射：B1 = issue 01；B2a/B2b = issue 02；B3 = issue 03；B4 = issue 04；B5 =
issue 05；B6 = issue 06；B7–B9 = issue 07。B1 与 B2a 可并行，B5 与 B6 可并行。

## 每个 Collector 的发布与更新路径

同一个 Registry feature 对不同 Execution Driver 有不同的真实成功判据；共享 Package publication，
不共享 Activation transaction 或 Last-Known-Good。

```mermaid
flowchart TD
    T["显式 collector-{name}/vX.Y.Z tag"]
    B["按 OS / arch 独立构建"]
    A["组装一个 canonical release"]
    V["验证 + Ed25519 签名"]
    W["发布 blob → release → stable channel"]
    O["Runtime 发现、下载、验证 offer"]
    Q{"owner 对该 Instance 批准"}

    MP["ManagedProcess 启动 exact hash"]
    MR["Ready"]
    MS["候选稳定窗口"]
    ML["晋升 per-Instance LKG"]

    BE["Browser side-by-side stage"]
    BR["owner reload extension"]
    BH["新 ExternalHost exact hash Ready"]
    BL["完成该 Instance 更新"]

    T --> B --> A --> V --> W --> O --> Q
    Q --> MP --> MR --> MS --> ML
    Q --> BE --> BR --> BH --> BL

    SYS["System Collector"] --> BUILTIN["Desktop build/release"]
    BUILTIN --> SR["Desktop Runtime Ready"]
```

失败边界保持不变：发现、下载、验证或 Activation 失败不改写 Desired State；ManagedProcess 稳定窗口
失败只回滚该 Instance；Browser 在新 exact hash Ready 前继续把旧 Host/LKG 视为真实运行状态。

## 推荐落地批次

1. **Batch 1（并行）**：strict ingest；InputEvent capacity；bounded drain；Registry contract；release
   pipeline scaffold。
2. **Batch 2（并行）**：Segment rotation + 长会话 E2E；canonical release publish；delivery deep module。
3. **Batch 3**：per-Instance offer/approval；同时完成 reliability restart/replay gate。
4. **Batch 4（并行）**：ManagedProcess adapter；Browser ExternalHost adapter。
5. **Batch 5（人工门禁）**：production signing key、同域名静态 Registry、真实安装 migration、两类
   Collector smoke 与 rollback。

每个 batch 都应保持 main 可构建、可回滚；不得为了赶下一批把 `downloaded`、`approved`、`Ready` 或
`stable` 合并成一个状态。
