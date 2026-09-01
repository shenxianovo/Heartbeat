# Collector 独立交付实现路线图

本文把 [ADR-045](../adr/045-independent-web-delivery-for-collector-packages.md) 的实现拆成两个已有
tracker 的 feature，并固定它们之间与各自内部的依赖顺序：

- [Collector 数据可靠性收口](../../.scratch/collector-reliability-closeout/PRD.md)
- [Collector Package Registry](../../.scratch/collector-package-registry/PRD.md)

箭头表示“右侧开始或完成前必须满足左侧条件”，不是要求所有工作串行。没有依赖箭头的节点可以并行。
Analytics + Dashboard 原子发布、Headless production configuration 是扫描发现的独立应用/运维平面
风险，不塞进这两个 Collection feature；它们必须在对应平面实际发布前单独 closeout。

> **当前开发路径（2026-08-31）**：ADR-047 已把 Feature B 第一条纵切缩减为 unsigned VRChat
> ManagedProcess delivery。下面 ADR-045 的生产目标仍保留作 later scope；当前 Agent 只执行“termination
> truth gate → 最小 index/tag pipeline → 版本目录安装 → exact ref approval → VRChat Ready → 开发域名
> smoke”，不得自行恢复签名、channel、solver、原子安装 journal、Browser 或全平台矩阵。

```mermaid
flowchart LR
    T["修正 termination cause<br/>durable evidence + deterministic tests"]
    I["per-Package current.json<br/>Version + URL + length + SHA-256"]
    B["VRChat 显式 tag build<br/>当前真实平台"]
    D["下载并安装到版本目录<br/>安全解压 + 完成标记"]
    A["authenticated Headless API<br/>手动 CheckNow + 批准 exact ref"]
    R["启动 VRChat candidate<br/>真实 Ready"]
    W["静态目录 + 开发域名 smoke<br/>失败保留旧 LKG"]

    T --> I
    T --> B
    I --> D
    B --> D
    D --> A --> R --> W
```

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

## Deferred production target

签名、channel、完整平台矩阵、Browser ExternalHost、离线目录、cache GC、生产迁移与运维演练只保留在
[ADR-045](../adr/045-independent-web-delivery-for-collector-packages.md) 作为未来目标，不进入当前 roadmap。
VRChat MVP 完成后必须重新 grill，不能从旧图直接恢复这些范围。
